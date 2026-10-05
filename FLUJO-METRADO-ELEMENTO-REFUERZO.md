# Flujo: texto propio en `Metrado - Elemento` y su acero de refuerzo

Cómo conseguir que, al cambiar a mano `Metrado - Elemento` en un elemento (por ejemplo `SOLADO` en un suelo),
las armaduras y mallas alojadas en él reciban el mismo texto y tengan su propia tabla y su propio filtro de
refuerzo. Todo se hace con el botón **Parámetros y filtros** (pestaña **ARBA**, panel **Metrados**).

Nada ocurre al editar el parámetro en Revit por sí solo: la copia al acero la hace la pestaña 1 cuando se
ejecuta.

## 1. Cambiar el texto en el elemento

En Revit, escribe el valor propio en `Metrado - Elemento` del suelo, viga, columna, cimentación o muro que
quieras separar: `SOLADO`, `ESCALERAS`, `MUROS DE CONTENCION`... Sirve desde la paleta de Propiedades o en
bloque desde una tabla de planificación que tenga esa columna.

Si el proyecto aún no tiene el parámetro, ejecuta antes la pestaña 1 una vez (crea los parámetros del
contrato ARBA y escribe los grupos estándar).

## 2. Pestaña 1 · "Escribir parámetros y filtros"

Pulsa **Parámetros y filtros**, pestaña **1. Parámetros y filtros (sin tablas)**, y ejecuta con estas tres
casillas marcadas (vienen así por defecto):

| Casilla | Para qué |
|---|---|
| **Conservar "Metrado - Elemento" ya escrito** | Solo rellena los elementos y armaduras que lo tengan vacío. Sin esto el plugin vuelve a escribir `LOSAS` en el suelo y pierdes tu texto. |
| **Incluir el acero de refuerzo** | Procesa armaduras y mallas (`Metrado - Elemento`, partición, `Metrado - Peso (kg)`). |
| **El refuerzo de los elementos cuyo "Metrado - Elemento" usted cambió a mano toma ese mismo texto** | Copia `SOLADO` a cada armadura y malla alojada en ese elemento. |

Qué hace el plugin por dentro, en este orden:

1. Rellena `Metrado - Elemento` en las armaduras que lo tengan vacío con el grupo de su anfitrión
   (`VIGAS`, `LOSAS`...). Con "Conservar" marcado no toca las que ya tienen texto.
2. `PropagarElementoDelAnfitrion` recorre todo el refuerzo: mira el anfitrión de cada barra y, si el texto del
   anfitrión es **distinto del grupo estándar** que el plugin le escribiría, copia ese texto en la barra.
   El acero de los elementos con grupo estándar no se toca.
3. El resumen final indica cuántos refuerzos recibieron el texto propio de su anfitrión
   ("Refuerzos con el 'Metrado - Elemento' propio de su anfitrión (cambiado a mano)").

## 3. Pestaña 2 · tablas y filtros propios

Vuelve a pulsar **Parámetros y filtros** y abre la pestaña **2. Tablas desde los parámetros**. El plugin lee
el modelo y muestra las combinaciones. Aparecen dos propias, ya marcadas:

| Combinación | Tipo | Origen |
|---|---|---|
| `CONCRETO` · `SOLADO` | Elementos | Propia |
| — · `SOLADO` | Refuerzo | Propia |

- **Crear tablas** genera `Metrado concreto - SOLADO` (elemento, material, cantidad, longitud, área, espesor,
  volumen) y `Metrado acero - SOLADO` (partición, tipo de barra, diámetro, N° barras, longitud total, peso
  unitario, peso en kg), filtradas por el valor exacto del parámetro. Si la combinación de elementos abarca
  varias categorías de Revit se crea una tabla por categoría.
- **Actualizar filtros** crea `Metrado - Concreto - SOLADO` y `Metrado - Refuerzo - SOLADO` con un color
  fijo derivado del nombre, y actualiza los filtros predeterminados. Como los filtros predeterminados exigen
  el valor exacto de `Metrado - Elemento`, el suelo con `SOLADO` deja de cumplir `Metrado - Concreto - Losas`
  y solo lo pinta su filtro propio.

## Detalles a tener en cuenta

- **Metrado automático deshace esto.** Siempre recalcula `Metrado - Elemento` en elementos y refuerzo, así
  que los textos propios vuelven a `LOSAS`. Para este flujo usa solo **Parámetros y filtros**.
- **Las tablas generales ya recogen el texto nuevo.** `Metrado acero - General`, `Metrado acero - Resumen`,
  `Metrado concreto - General` y `Metrado acero estructural - General` agrupan por `Metrado - Elemento`, así
  que `SOLADO` aparece como grupo propio en ellas sin crear nada más (son tablas vivas de Revit).
- **Solo cuenta el refuerzo alojado.** La copia usa el anfitrión real de cada armadura o malla. Una barra sin
  anfitrión queda como `(SIN ANFITRIÓN)` y no recibe texto propio.
- **Volver atrás no es automático.** Si cambias el suelo de `SOLADO` a `LOSAS`, sus barras conservan `SOLADO`,
  porque "Conservar" solo rellena vacíos y la propagación ignora los textos estándar. Para devolverlas, cambia
  el parámetro de esas barras a mano, o ejecuta la pestaña 1 una vez sin "Conservar Metrado - Elemento",
  sabiendo que eso recalcula también los demás elementos y armaduras.
- **Repetir el paso 1 es seguro.** Con las casillas "Conservar" marcadas puedes ejecutarlo cada vez que
  modeles elementos nuevos o cambies más textos: respeta lo ya escrito y propaga lo nuevo.
