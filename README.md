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
- Botón **Metrado automático**: calcula el concreto (m³) y el acero de refuerzo (kg) de vigas, columnas y
  otros elementos estructurales leyendo directamente el modelo, sin necesitar tablas de planificación.
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
│       ├── CalculadorMetrado.cs   Recorre el modelo: volúmenes de concreto y barras de acero
│       ├── ExportadorMetrado.cs   Escribe las hojas Resumen, Concreto, Acero y detalle
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

**Tablas que crea en Revit** (una de concreto y una de acero por cada tipo de elemento marcado):

| Tabla | Categoría | Campos | Agrupación |
|---|---|---|---|
| `Metrado concreto - Vigas` | Armazón estructural | Nivel, Elemento (familia y tipo), Material, Cantidad, Longitud, Volumen | Por nivel (encabezado, pie con totales), luego tipo; total general |
| `Metrado concreto - Columnas` | Pilares estructurales | Nivel base, Elemento, Material, Cantidad, Longitud, Volumen | Igual |
| `Metrado concreto - Losas` | Suelos | Nivel, Elemento, Cantidad, Área, Espesor, Volumen | Igual |
| `Metrado concreto - Cimentaciones` / `Muros` | Opcionales | Nivel, Elemento, Material, Cantidad, Área/Longitud, Espesor, Volumen | Igual |
| `Metrado acero - <elemento>` | Armadura estructural | Partición, Elemento anfitrión, Marca anfitrión, Tipo de barra, Diámetro, N° barras, Longitud de barra, Longitud total, Peso unitario | Por partición (encabezado, pie con totales), luego diámetro; total general. Filtrada por categoría del anfitrión |

- Las tablas no están desglosadas por elemento (una fila por tipo y nivel). Si quiere ver cada elemento,
  active "Desglosar cada ejemplar" en la tabla.
- Las tablas de concreto llevan un filtro "Material estructural contiene *Concreto*" (texto editable en la
  ventana; use "Hormigón" si sus materiales se llaman así). Puede desactivarse.
- **Peso del acero**: la tabla de Revit incluye la columna con el parámetro de peso por metro del tipo de
  barra (por defecto `Bar Mass per Unit Length`, editable en la ventana). Revit no permite crear valores
  calculados desde la API, así que la columna "Peso total = Longitud total × Peso unitario" debe añadirse una
  sola vez a mano en la tabla (Campos → Valor calculado). Como el plugin **reutiliza** las tablas existentes
  en lugar de recrearlas, esa columna se conserva en las siguientes ejecuciones y se exporta a Excel.
- Si ya existe una tabla con el mismo nombre se reutiliza tal cual. La opción "Regenerar" la borra y la
  crea de nuevo (se pierden columnas añadidas a mano y su colocación en planos).
- Si la versión de Revit no permite filtrar el acero por categoría del anfitrión, se crea una sola tabla
  `Metrado acero` agrupada por partición.

**Exportación a Excel en la misma operación** (opcional): el libro contiene

| Hoja | Contenido |
|---|---|
| Resumen | Concreto (m³), acero (kg) y cuantía (kg/m³) por tipo de elemento y acero total por diámetro, calculados directamente del modelo. |
| Una hoja por tabla de Revit | El contenido de cada tabla generada, tal como se ve en Revit (incluidas las columnas que haya añadido a mano). |
| Concreto - Detalle / Acero - Detalle | Opcional. Una fila por elemento o conjunto de barras con Id, nivel, tipo, longitudes, área, espesor, volumen y peso. |

**Cómo calcula el resumen** (independiente de las tablas, leyendo el modelo):

- Volumen de concreto: suma del volumen de cada material del elemento que sea de concreto (clase o nombre con
  "concreto", "hormigón", "concrete", "f'c", o activo estructural de clase Concrete). En losas y muros
  compuestos excluye acabados y otras capas. Si el elemento no tiene materiales asignados se usa el
  material estructural y el parámetro Volumen.
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
