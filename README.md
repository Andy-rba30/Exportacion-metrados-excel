# Exportación de Metrados a Excel (plugin para Revit)

Plugin para Autodesk Revit, escrito en C#, que exporta las **tablas de planificación / metrados**
(`ViewSchedule`) del proyecto a un libro de Excel (`.xlsx`), con una hoja por tabla.

- No requiere tener Microsoft Excel instalado (usa [ClosedXML](https://github.com/ClosedXML/ClosedXML)).
- Exporta las celdas **tal como se ven en Revit**: unidades, redondeo, totales, agrupaciones y filas
  de subtotal, porque lee el texto renderizado de la tabla con `ViewSchedule.GetCellText`.
- Convierte los valores numéricos a números reales de Excel conservando la unidad como formato de celda
  (por ejemplo `12.50 m²` queda como `12.5` con formato `#,##0.00 "m²"`), para que se puedan sumar
  y usar en fórmulas.
- Ventana de selección con buscador, selección múltiple y opciones de formato.
- Botón **Metrado automático**: calcula el concreto (m³), los perfiles metálicos (kg, por longitud × área de
  sección × densidad) y el acero de refuerzo (kg) de vigas, columnas y otros elementos estructurales leyendo
  directamente el modelo, sin necesitar tablas de planificación.
- Compatible con Revit 2021 a 2024 (.NET Framework 4.8), 2025 y 2026 (.NET 8) y 2027+ (.NET 10).

## Estructura

```
ExportacionMetrados.sln
src/ExportacionMetrados/
├── App.cs                         Crea la pestaña "Metrados" y el botón en la cinta
├── ExportarMetradosCommand.cs     Comando 1: exporta las tablas de planificación elegidas
├── MetradoAutomaticoCommand.cs    Comando 2: metrado automático de concreto y acero
├── ExportacionMetrados.addin      Manifiesto que Revit lee para cargar el plugin
├── Core/
│   ├── LectorTablas.cs            Lee las tablas de Revit (encabezados y cuerpo)
│   ├── ExportadorExcel.cs         Escribe el .xlsx con ClosedXML
│   ├── OpcionesExportacion.cs     Opciones y resultado de la exportación
│   └── Metrado/
│       ├── CalculadorMetrado.cs   Recorre el modelo: volúmenes de concreto, peso de perfiles y barras de acero
│       ├── ClasificadorElementos.cs Parámetros "Metrado - Material" y "Metrado - Peso (kg)", particiones
│       ├── GeneradorTablasRevit.cs Crea las tablas de planificación de metrado en el proyecto
│       ├── ExportadorMetrado.cs   Escribe las hojas Resumen, Concreto, Acero estructural, Acero y detalle
│       └── ModelosMetrado.cs      Opciones, categorías y resultados del metrado
├── UI/
│   ├── SeleccionTablasWindow.xaml Ventana de selección de tablas
│   ├── MetradoAutomaticoWindow.xaml Ventana de opciones del metrado automático
│   └── TablaItem.cs               Modelo de cada fila de la lista
└── Resources/                     Iconos del botón
```

## Requisitos

- Windows con Autodesk Revit instalado (2021 o superior).
- [SDK de .NET](https://dotnet.microsoft.com/download) acorde a la versión de Revit: .NET 10 para Revit 2027,
  .NET 8 para 2025/2026 (el SDK 10 también compila esos destinos). Para Revit 2021-2024 basta con el SDK y el
  paquete de destino de .NET Framework 4.8 que instala Visual Studio 2022.

## Compilación e instalación

Desde una terminal en la raíz del repositorio:

```powershell
# Revit 2027 (valor por defecto, .NET 10)
dotnet build -c Release

# Otra versión de Revit
dotnet build -c Release -p:RevitVersion=2026
dotnet build -c Release -p:RevitVersion=2024
```

El framework se elige automáticamente según la versión (2021-2024: net48; 2025-2026: net8.0-windows;
2027+: net10.0-windows). Si Revit 2027 en su equipo usa otro runtime, fuércelo:

```powershell
dotnet build -c Release -p:RevitVersion=2027 -p:TargetFramework=net8.0-windows
```

Para saber qué runtime usa su Revit, mire la versión de `coreclr.dll` en la carpeta de instalación de Revit
o el valor de `Microsoft.NETCore.App` en `Revit.runtimeconfig.json` dentro de esa misma carpeta.

El proyecto busca `RevitAPI.dll` y `RevitAPIUI.dll` en `C:\Program Files\Autodesk\Revit <versión>\`.
Si Revit está en otra ruta, pase la propiedad `RevitInstallDir`:

```powershell
dotnet build -c Release -p:RevitVersion=2027 -p:RevitInstallDir="D:\Autodesk\Revit 2027"
```

Al terminar la compilación el plugin se copia automáticamente a la carpeta de add-ins del usuario:

```
%AppData%\Autodesk\Revit\Addins\<versión>\ExportacionMetrados.addin
%AppData%\Autodesk\Revit\Addins\<versión>\ExportacionMetrados\*.dll
```

(Se puede desactivar la copia con `-p:DeployToRevit=false`.)

Reinicie Revit. La primera vez Revit preguntará si confía en el complemento; elija **Cargar siempre**.

### Instalación manual

Si prefiere no usar la copia automática, copie `ExportacionMetrados.addin` a
`%AppData%\Autodesk\Revit\Addins\<versión>\` y el contenido de `bin\Release\` a una subcarpeta
`ExportacionMetrados` dentro de esa misma ruta.

## Uso

1. Abra el proyecto en Revit.
2. Vaya a la pestaña **Metrados**. Hay dos botones: **Exportar a Excel** (exporta tablas de planificación
   ya existentes) y **Metrado automático** (crea las tablas de metrado en Revit y opcionalmente las exporta,
   ver más abajo). Pulse **Exportar a Excel**.
3. Marque las tablas que desea exportar (si la vista activa es una tabla, aparece marcada).
   Puede filtrar por nombre o categoría y usar **Todas** / **Ninguna**.
4. Ajuste las opciones:
   - Incluir el nombre de la tabla como título.
   - Incluir encabezados de columna.
   - Convertir valores numéricos (conserva la unidad como formato).
   - Aplicar formato (negrita, bordes, anchos de columna, encabezado fijo).
5. Elija el archivo de destino y pulse **Exportar**.

Cada tabla se escribe en su propia hoja. El nombre de la hoja es el de la tabla, recortado a 31 caracteres
y sin los caracteres que Excel no admite (`[ ] * ? / \ :`); si hay nombres repetidos se añade `(2)`, `(3)`, etc.

## Metrado automático de concreto y acero

El botón **Metrado automático** crea las tablas de planificación de metrado **dentro del proyecto de Revit**
y, si se marca la opción, las exporta a Excel en la misma operación. (El botón *Exportar a Excel* es una
función aparte para tablas que ya existen en el proyecto.)

**Tablas que crea en Revit** por cada tipo de elemento marcado:

| Tabla | Contenido | Agrupación |
|---|---|---|
| `Metrado concreto - Vigas` | Elemento (familia y tipo), Material, Cantidad, Longitud, Volumen. Solo elementos con material de concreto. | Por tipo; total general. **Sin niveles** (una viga puede cruzar varios). |
| `Metrado concreto - Columnas` / `Losas` / `Cimentaciones` / `Muros` | Nivel, Elemento, Material, Cantidad, Longitud o Área, Espesor, Volumen | Por nivel (encabezado y pie con totales), luego tipo; total general |
| `Metrado acero estructural - <elemento>` | Elementos cuyo material **no** es concreto (perfiles metálicos): Elemento, Material, Cantidad, Longitud, Área de sección, **Peso (kg)**. Los perfiles no se metran por volumen sino por peso. Solo se crea si existen. | Igual |
| `Metrado acero - <elemento>` | Refuerzo con partición `VIGAS`, `COLUMNAS`, etc.: Partición, Tipo de barra, Diámetro, N° barras, Longitud total, Peso unitario, Peso (kg) | Por partición (encabezado y pie con totales), luego tipo de barra; total general |
| `Metrado acero - General` | Todo el refuerzo del modelo, mismas columnas | Por partición, luego tipo de barra; total general |

- Las tablas no están desglosadas por elemento (una fila por tipo). Si quiere ver cada elemento, active
  "Desglosar cada ejemplar" en la tabla.
- **Separación concreto / metálico**: el plugin crea el parámetro de proyecto **"Metrado - Material"**
  (texto, de ejemplar) en vigas, columnas, losas, cimentaciones y muros, y lo rellena con `CONCRETO`,
  `ACERO ESTRUCTURAL`, `MADERA` u `OTRO`. Para clasificar usa, en este orden: el "Material para
  comportamiento del modelo" de la familia, los materiales del elemento, y el nombre de la familia o tipo
  (perfiles HSS, W, C, L, IPE...). Las tablas de concreto filtran `= CONCRETO` y las de acero estructural
  `≠ CONCRETO`. Si un elemento quedó mal clasificado, corrija el valor del parámetro en sus propiedades y
  marque "Conservar la clasificación ya escrita" en la siguiente ejecución.
- **Partición del acero**: antes de crear las tablas, el plugin escribe en la partición vacía de cada
  armadura el nombre de la categoría de su anfitrión (`VIGAS`, `COLUMNAS`, `CIMIENTOS`, `LOSAS`, `MUROS`).
  Las tablas de acero por elemento filtran por ese texto. Las particiones que ya tienen texto se respetan
  salvo que marque "Sobrescribir".
- **Peso del acero**: Revit no permite crear valores calculados desde la API, así que el plugin crea el
  parámetro de proyecto **"Metrado - Peso (kg)"** en las armaduras, vigas y columnas y lo rellena en cada
  ejecución. En las armaduras vale `Longitud total × peso por metro` (el peso por metro sale del parámetro
  del tipo de barra, por defecto `Bar Mass per Unit Length`, o de π·d²/4 × densidad si no existe). Las
  tablas muestran esa columna con totales. Si modifica armaduras después, vuelva a ejecutar el metrado
  para actualizar los pesos.
- **Peso de los perfiles metálicos**: las vigas y columnas clasificadas como `ACERO ESTRUCTURAL` no se
  metran por volumen sino por peso: `Longitud × área de sección × densidad`. El área de sección se lee del
  parámetro **Área de sección** del tipo (perfiles con sección estructural: W, HSS, IPE, C, L...), de la
  definición de sección estructural de la familia o de un parámetro de área con nombre habitual; como
  último recurso se usa `Volumen / longitud`. La densidad es la del **acero al carbono**, material de los
  perfiles estructurales: 7850 kg/m³ por defecto, ajustable en la ventana. El resultado se escribe en
  "Metrado - Peso (kg)" de cada perfil y la tabla `Metrado acero estructural - <elemento>` lo suma.
- Si ya existe una tabla con el mismo nombre se reutiliza tal cual. La opción "Regenerar" la borra y la
  crea de nuevo (se pierden columnas añadidas a mano y su colocación en planos). Tras actualizar el plugin
  conviene regenerar una vez para obtener la nueva estructura.

**Exportación a Excel en la misma operación** (opcional): el libro contiene

| Hoja | Contenido |
|---|---|
| Resumen | Concreto (m³), acero (kg) y cuantía (kg/m³) por tipo de elemento, perfiles metálicos (longitud y kg por tipo de elemento) y acero total por diámetro, calculados directamente del modelo. |
| Una hoja por tabla de Revit | El contenido de cada tabla generada, tal como se ve en Revit (incluidas las columnas que haya añadido a mano). |
| Concreto - Detalle / Acero estructural - Detalle / Acero - Detalle | Opcional. Una fila por elemento, perfil o conjunto de barras con Id, nivel, tipo, longitudes, área, espesor, volumen, área de sección, densidad y peso. |

**Cómo calcula el resumen** (independiente de las tablas, leyendo el modelo):

- Volumen de concreto: suma del volumen de cada material del elemento que sea de concreto (clase o nombre con
  "concreto", "hormigón", "concrete", "f'c", o activo estructural de clase Concrete). En losas y muros
  compuestos excluye acabados y otras capas. Si el elemento no tiene materiales asignados se usa el
  material estructural y el parámetro Volumen.
- Perfiles metálicos: vigas y columnas clasificadas como `ACERO ESTRUCTURAL`. Peso = longitud × área de
  sección × densidad del acero al carbono (7850 kg/m³ por defecto). No entran en el volumen de concreto.
- Nivel: nivel de referencia (vigas), nivel base (columnas, muros) o el nivel del elemento.
- Acero: barras (`Rebar`), refuerzo por área y trayectoria (`RebarInSystem`) y mallas electrosoldadas
  (`FabricSheet`) cuyo anfitrión pertenece a las categorías marcadas. La longitud se toma del parámetro
  **Longitud total de barra** (`Total Bar Length`), que suma todas las piezas del conjunto con sus ganchos y
  dobleces, y no de **Longitud de barra** (`Bar Length`), que es la de una sola pieza. Si falta, se calcula
  la longitud geométrica del eje de cada posición de barra y, como último recurso, longitud de barra ×
  cantidad.
- Peso: `Longitud total × peso por metro`. El peso por metro se lee del parámetro del tipo de barra indicado
  en la ventana (`Bar Mass per Unit Length` por defecto; se respetan sus unidades si es de disciplina
  "masa por unidad de longitud"). Si el tipo no tiene ese parámetro se calcula como π·d²/4 × densidad
  (7850 kg/m³ por defecto). Las mallas usan la masa de hoja cortada que calcula Revit.

## Asignar partición (tercer botón)

Escribe el parámetro **Partición** del acero de refuerzo sin pasar por el metrado:

- **A qué**: la selección actual (anfitriones y/o armaduras), elementos elegidos en pantalla, o todo el modelo.
  Si selecciona una viga, se asigna a todas las armaduras alojadas en ella.
- **Qué texto**: automático según la categoría del anfitrión (`VIGAS`, `COLUMNAS`, `CIMIENTOS`, `LOSAS`,
  `MUROS`) o un texto propio, por ejemplo `VIGA V-101` o `BLOQUE A - COLUMNAS`.
- Opción para sobrescribir o respetar las particiones que ya tengan texto.

Las tablas de acero y la general se agrupan por este parámetro, así que basta con mantenerlo al día.

## Notas técnicas

- El comando de exportación se declara con `TransactionMode.ReadOnly`: no modifica el modelo. El metrado
  automático usa una transacción propia ("Metrado automático") solo para crear las tablas; se puede deshacer
  con Ctrl+Z.
- Las filas completamente en blanco (separadores entre grupos) se omiten.
- Si una tabla tiene desactivada la opción "Mostrar encabezados", se usan los encabezados de columna
  definidos en los campos de la tabla.
- Conversión numérica: se aceptan separadores de miles y decimales en formato español (`1.234,56`) e
  inglés (`1,234.56`). Un valor con un único separador seguido de tres dígitos (`1.250`) es ambiguo y se
  interpreta según la configuración regional de Windows.
- Los textos que empiezan por `=` se escriben como texto, nunca como fórmula.
- El archivo se escribe primero como `.tmp` y luego se renombra, para no dejar un `.xlsx` dañado si el
  destino está abierto en Excel.

## Licencia

MIT.
