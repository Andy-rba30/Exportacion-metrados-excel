using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Punto de entrada del plugin. Crea la pestaña "Metrados" en la cinta de
    /// Revit con el botón para exportar tablas de planificación a Excel.
    /// </summary>
    public class App : IExternalApplication
    {
        private const string NombrePestana = "Metrados";
        private const string NombrePanel = "Exportar";

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
            // La pestaña puede existir si otro plugin la creó; en ese caso se reutiliza.
            try { application.CreateRibbonTab(NombrePestana); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }

            RibbonPanel panel = null;
            foreach (var p in application.GetRibbonPanels(NombrePestana))
            {
                if (p.Name == NombrePanel) { panel = p; break; }
            }
            if (panel == null)
            {
                panel = application.CreateRibbonPanel(NombrePestana, NombrePanel);
            }

            string rutaEnsamblado = Assembly.GetExecutingAssembly().Location;

            var datosBoton = new PushButtonData(
                "ExportarMetradosExcel",
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
                "MetradoAutomatico",
                "Metrado\nautomático",
                rutaEnsamblado,
                typeof(MetradoAutomaticoCommand).FullName)
            {
                ToolTip = "Crea en el proyecto las tablas de metrado de concreto y acero (vigas, columnas, losas...) y opcionalmente las exporta a Excel.",
                LongDescription = "Genera una tabla de planificación de concreto y otra de acero por cada tipo de elemento, agrupadas por nivel " +
                                  "y partición, con totales. En la misma operación puede exportarlas a Excel junto con un resumen en m³ y kg.",
                LargeImage = CargarIcono("metrado32.png"),
                Image = CargarIcono("metrado16.png"),
            };

            var datosParticion = new PushButtonData(
                "AsignarParticion",
                "Asignar\npartición",
                rutaEnsamblado,
                typeof(AsignarParticionCommand).FullName)
            {
                ToolTip = "Escribe la partición del acero de refuerzo (VIGAS, COLUMNAS, CIMIENTOS, LOSAS...) a la selección, por lotes o a todo el modelo.",
                LongDescription = "Seleccione elementos anfitriones o armaduras y el plugin rellena su parámetro Partición con el nombre " +
                                  "de la categoría del anfitrión o con un texto propio. Así las tablas de acero se agrupan correctamente.",
                LargeImage = CargarIcono("particion32.png"),
                Image = CargarIcono("particion16.png"),
            };

            panel.AddItem(datosBoton);
            panel.AddItem(datosMetrado);
            panel.AddItem(datosParticion);
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
