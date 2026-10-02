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
                ToolTip = "Calcula el concreto (m³) y el acero (kg) de vigas, columnas y otros elementos estructurales y lo exporta a Excel.",
                LongDescription = "Lee los elementos del modelo directamente: volumen de concreto agrupado por elemento, nivel y tipo, " +
                                  "y acero de refuerzo agrupado por elemento anfitrión, nivel y diámetro. No requiere tablas de planificación.",
                LargeImage = CargarIcono("metrado32.png"),
                Image = CargarIcono("metrado16.png"),
            };

            panel.AddItem(datosBoton);
            panel.AddItem(datosMetrado);
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
