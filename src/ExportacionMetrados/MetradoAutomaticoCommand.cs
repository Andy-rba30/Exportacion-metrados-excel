using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ExportacionMetrados.Core.Metrado;
using ExportacionMetrados.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Comando que calcula automáticamente el metrado de concreto y acero del
    /// modelo (vigas, columnas, etc.) y lo exporta a Excel.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class MetradoAutomaticoCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Metrado automático", "Abra un proyecto de Revit antes de ejecutar el comando.");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;

            try
            {
                var ventana = new MetradoAutomaticoWindow(SugerirNombreArchivo(doc));
                _ = new System.Windows.Interop.WindowInteropHelper(ventana)
                {
                    Owner = commandData.Application.MainWindowHandle
                };

                if (ventana.ShowDialog() != true) return Result.Cancelled;

                OpcionesMetrado opciones = ventana.Opciones;

                var calculador = new CalculadorMetrado(doc, opciones);
                ResultadoMetrado resultado = calculador.Calcular();

                if (resultado.Concreto.Count == 0 && resultado.Acero.Count == 0)
                {
                    TaskDialog.Show("Metrado automático",
                        "No se encontraron elementos de concreto ni acero en las categorías seleccionadas.\n\n" +
                        string.Join("\n", resultado.Advertencias));
                    return Result.Cancelled;
                }

                new ExportadorMetrado(opciones).Exportar(resultado, doc.Title);

                MostrarResumen(resultado, opciones);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Metrado automático", "Ocurrió un error durante el metrado:\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static string SugerirNombreArchivo(Document doc)
        {
            string nombre = string.IsNullOrWhiteSpace(doc.Title) ? "Proyecto" : doc.Title;
            foreach (char c in Path.GetInvalidFileNameChars()) nombre = nombre.Replace(c, '_');
            return nombre + " - Metrado concreto y acero.xlsx";
        }

        private static void MostrarResumen(ResultadoMetrado resultado, OpcionesMetrado opciones)
        {
            double m3 = 0;
            foreach (var c in resultado.Concreto) m3 += c.VolumenM3;
            double kg = 0;
            foreach (var a in resultado.Acero) kg += a.PesoKg;

            var dialogo = new TaskDialog("Metrado automático")
            {
                MainInstruction = resultado.Advertencias.Count == 0
                    ? "Metrado completado"
                    : "Metrado completado con advertencias",
                MainContent =
                    $"Elementos de concreto: {resultado.Concreto.Count}  ({m3:N3} m³)\n" +
                    $"Conjuntos de barras: {resultado.Acero.Count}  ({kg:N2} kg)\n\n" +
                    $"Archivo:\n{opciones.RutaArchivo}",
                CommonButtons = TaskDialogCommonButtons.Close,
            };

            if (resultado.Advertencias.Count > 0)
            {
                dialogo.ExpandedContent = string.Join("\n", resultado.Advertencias);
            }
            if (opciones.AbrirAlTerminar)
            {
                dialogo.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir el archivo");
            }

            if (dialogo.Show() == TaskDialogResult.CommandLink1)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(opciones.RutaArchivo)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Metrado automático", "No se pudo abrir el archivo:\n" + ex.Message);
                }
            }
        }
    }
}
