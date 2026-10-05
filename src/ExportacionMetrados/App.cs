using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Arba.Comun;
using Autodesk.Revit.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Punto de entrada del plugin. Añade al panel "Metrados" de la pestaña común "ARBA"
    /// (contrato ARBA-comun) los botones de exportar a Excel, metrado automático, parámetros y
    /// filtros (sin tablas, y tablas propias desde los parámetros), asignar partición y migrar
    /// particiones y origen. La pestaña y sus paneles los crea
    /// <see cref="ArbaRibbon"/>, el mismo código que usan los add-ins de armado, así todos
    /// comparten una sola pestaña sin importar cuál cargue primero.
    /// </summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                CrearCinta(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Exportación de Metrados",
                    "No se pudo inicializar el plugin:\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private static void CrearCinta(UIControlledApplication application)
        {
            // Pestaña "ARBA" y paneles IA / Acero / Metrados / Encofrado en orden (ocultos hasta tener botones).
            ArbaRibbon.Ensure(application);

            string rutaEnsamblado = Assembly.GetExecutingAssembly().Location;

            var datosExportar = new PushButtonData(
                "ARBA_Metrados_Exportar",
                "Exportar a\nExcel",
                rutaEnsamblado,
                typeof(ExportarMetradosCommand).FullName)
            {
                ToolTip = "Exporta las tablas de planificación (metrados) del proyecto a un libro de Excel.",
                LongDescription = "Selecciona una o varias tablas de planificación y genera un archivo .xlsx " +
                                  "con una hoja por tabla. No requiere tener Excel instalado.",
                LargeImage = CargarIcono("icono32.png"),
                Image = CargarIcono("icono16.png"),
            };

            var datosMetrado = new PushButtonData(
                "ARBA_Metrados_Automatico",
                "Metrado\nautomático",
                rutaEnsamblado,
                typeof(MetradoAutomaticoCommand).FullName)
            {
                ToolTip = "Crea en el proyecto las tablas de metrado de concreto y acero (vigas, columnas, losas, misceláneos...) y opcionalmente las exporta a Excel.",
                LongDescription = "Genera una tabla de planificación de concreto y otra de acero por cada tipo de elemento, agrupadas por nivel " +
                                  "y partición, con totales, más la tabla de misceláneos por partida (rejillas, ángulos). Crea los parámetros " +
                                  "compartidos del contrato ARBA-comun " + ArbaContract.Version + " si faltan. En la misma operación puede " +
                                  "exportarlas a Excel junto con un resumen en m³ y kg.",
                LargeImage = CargarIcono("metrado32.png"),
                Image = CargarIcono("metrado16.png"),
            };

            var datosParametros = new PushButtonData(
                "ARBA_Metrados_Parametros",
                "Parámetros\ny filtros",
                rutaEnsamblado,
                typeof(ParametrosMetradoCommand).FullName)
            {
                ToolTip = "Escribe los parámetros del metrado (\"Metrado - Material\", \"Metrado - Elemento\", \"Metrado - Peso (kg)\", particiones) " +
                          "y crea los filtros de vista por colores sin crear tablas; después crea tablas propias con los valores que usted " +
                          "haya puesto en esos parámetros.",
                LongDescription = "Paso 1: lo mismo que el metrado automático pero sin tablas ni Excel, para poder cambiar a mano " +
                                  "\"Metrado - Material\" y \"Metrado - Elemento\" en los elementos que quiera (por ejemplo ESCALERAS). " +
                                  "Paso 2: lee los valores de esos dos parámetros que hay en el modelo y crea una tabla de planificación " +
                                  "por cada combinación elegida, filtrada por esos valores, aparte de las tablas predeterminadas y sin " +
                                  "reescribir ningún parámetro.",
                LargeImage = ArbaRibbon.IconMetrados(32),
                Image = ArbaRibbon.IconMetrados(16),
            };

            var datosParticion = new PushButtonData(
                "ARBA_Metrados_Particion",
                "Asignar\npartición",
                rutaEnsamblado,
                typeof(AsignarParticionCommand).FullName)
            {
                ToolTip = "Escribe la partición del acero de refuerzo que no creó ningún add-in ARBA con la forma del contrato " +
                          "(\"VIGAS - MAN-V1\", \"CIMIENTOS - MAN-Z3\"...) y \"ARBA - Origen\" = MANUAL, a la selección, por lotes o a todo el modelo.",
                LongDescription = "Seleccione elementos anfitriones o armaduras y el plugin rellena su parámetro Partición con " +
                                  "\"CATEGORIA - MAN-marca\" según la categoría y la marca del anfitrión, o con un texto propio. " +
                                  "Las armaduras creadas por los add-ins ARBA (ZAP, CCO, BLQ, VIG, COL, LOS, MCO) no se tocan. " +
                                  "Así las tablas de acero se agrupan correctamente.",
                LargeImage = CargarIcono("particion32.png"),
                Image = CargarIcono("particion16.png"),
            };

            var datosMigrar = new PushButtonData(
                "ARBA_Metrados_Migrar",
                "Migrar\nparticiones y origen",
                rutaEnsamblado,
                typeof(MigrarParticionesCommand).FullName)
            {
                ToolTip = "Convierte las particiones antiguas de los add-ins ARBA (ZAP-Z1, CC-C1, BLQ-FT-01-F1, LOSA-L1, MC-M1...) a la " +
                          "forma del contrato \"CATEGORIA - PREFIJO-marca[-codigo]\" y rellena \"ARBA - Origen\", \"ARBA - Código\" y " +
                          "\"Metrado - Elemento\". Sin selección migra todo el modelo; con selección, los anfitriones elegidos.",
                LongDescription = "La categoría se toma del anfitrión real: ZAP-Z1 pasa a CIMIENTOS - ZAP-Z1, CC-C1 a MUROS - CCO-C1 " +
                                  "(o CIMIENTOS - CCO-C1), BLQ-FT-01-F1 a CIMIENTOS - BLQ-FT-01-F1, LOSA-L1 a LOSAS - LOS-L1 y MC-M1 a " +
                                  "MUROS - MCO-M1. No toca las particiones de solo categoría (VIGAS) ni las desconocidas, no crea ni " +
                                  "borra barras y crea los parámetros compartidos del contrato ARBA-comun " + ArbaContract.Version +
                                  " si faltan. Se puede deshacer con Ctrl+Z.",
                LargeImage = ArbaRibbon.IconMetrados(32),
                Image = ArbaRibbon.IconMetrados(16),
            };

            ArbaRibbon.AddMetrados(application, datosExportar);
            ArbaRibbon.AddMetrados(application, datosMetrado);
            ArbaRibbon.AddMetrados(application, datosParametros);
            ArbaRibbon.AddMetrados(application, datosParticion);
            ArbaRibbon.AddMetrados(application, datosMigrar);
        }

        private static BitmapImage CargarIcono(string nombre)
        {
            try
            {
                var uri = new Uri(
                    $"pack://application:,,,/{Assembly.GetExecutingAssembly().GetName().Name};component/Resources/{nombre}",
                    UriKind.Absolute);
                return new BitmapImage(uri);
            }
            catch
            {
                // Si falta el icono, el botón se muestra solo con texto.
                return null;
            }
        }
    }
}
