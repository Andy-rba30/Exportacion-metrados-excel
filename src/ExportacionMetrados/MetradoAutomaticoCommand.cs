using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ExportacionMetrados.Core.Metrado;
using ExportacionMetrados.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Comando que genera en el proyecto las tablas de planificación de metrado
    /// (concreto y acero) y, opcionalmente, las exporta a Excel en la misma operación.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
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
                var advertencias = new List<string>();

                // 1. Tablas de planificación en Revit (requiere transacción).
                List<ViewSchedule> tablas;
                GeneradorTablasRevit generador;
                int clasificados = 0, particionados = 0;
                using (var t = new Transaction(doc, "Metrado automático"))
                {
                    t.Start();

                    // 1a. Parámetro "Metrado - Material" y clasificación concreto / metálico.
                    var categoriasBic = opciones.Categorias.Where(c => c.Seleccionada).Select(c => c.Categoria).ToList();
                    if (ClasificadorElementos.AsegurarParametroMaterial(doc, categoriasBic, advertencias))
                    {
                        doc.Regenerate();
                        clasificados = ClasificadorElementos.RellenarMaterial(doc, categoriasBic, opciones.ConservarClasificacionMaterial, advertencias);
                    }

                    // 1b. Partición del refuerzo según la categoría del anfitrión.
                    if (opciones.IncluirAcero && opciones.RellenarParticiones)
                    {
                        particionados = ClasificadorElementos.AsignarParticion(doc, ClasificadorElementos.TodoElRefuerzo(doc),
                            opciones.Categorias, opciones.SobrescribirParticiones, null, advertencias);
                    }

                    // 1c. Tablas.
                    generador = new GeneradorTablasRevit(doc, opciones, uidoc.ActiveView?.Id);
                    tablas = generador.Generar();
                    t.Commit();
                }
                advertencias.AddRange(generador.Advertencias);

                // 2. Cálculo directo del modelo (resumen con m³ y kg).
                ResultadoMetrado resultado = new CalculadorMetrado(doc, opciones).Calcular();
                advertencias.AddRange(resultado.Advertencias);

                // 3. Excel opcional: hojas con las tablas de Revit + resumen.
                if (opciones.ExportarExcel)
                {
                    var errores = new ExportadorMetrado(opciones).Exportar(resultado, doc.Title, tablas);
                    advertencias.AddRange(errores);
                }

                // 4. Abrir la primera tabla en Revit.
                if (opciones.AbrirTablaAlTerminar && tablas.Count > 0)
                {
                    try { uidoc.ActiveView = tablas[0]; }
                    catch (Exception) { /* no es crítico */ }
                }

                MostrarResumen(generador, tablas, resultado, opciones, advertencias, clasificados, particionados);
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

        private static void MostrarResumen(GeneradorTablasRevit generador, List<ViewSchedule> tablas,
            ResultadoMetrado resultado, OpcionesMetrado opciones, List<string> advertencias, int clasificados, int particionados)
        {
            double m3 = resultado.Concreto.Sum(c => c.VolumenM3);
            double kg = resultado.Acero.Sum(a => a.PesoKg);

            string contenido =
                $"Tablas creadas en Revit: {generador.TablasCreadas.Count}\n" +
                $"Tablas existentes reutilizadas: {generador.TablasReutilizadas.Count}\n" +
                $"Elementos clasificados (Metrado - Material): {clasificados}\n" +
                $"Refuerzos con partición asignada: {particionados}\n\n" +
                $"Concreto: {resultado.Concreto.Count} elementos, {m3:N3} m³\n" +
                $"Acero: {resultado.Acero.Count} conjuntos de barras, {kg:N2} kg\n";

            if (opciones.ExportarExcel)
            {
                contenido += $"\nArchivo Excel:\n{opciones.RutaArchivo}";
            }

            var dialogo = new TaskDialog("Metrado automático")
            {
                MainInstruction = advertencias.Count == 0 ? "Metrado completado" : "Metrado completado con advertencias",
                MainContent = contenido,
                CommonButtons = TaskDialogCommonButtons.Close,
            };

            var detalle = new List<string>();
            if (tablas.Count > 0)
            {
                detalle.Add("Tablas:");
                detalle.AddRange(tablas.Select(t => "  • " + t.Name));
            }
            if (advertencias.Count > 0)
            {
                detalle.Add("");
                detalle.Add("Advertencias:");
                detalle.AddRange(advertencias.Select(a => "  • " + a));
            }
            if (detalle.Count > 0) dialogo.ExpandedContent = string.Join("\n", detalle);

            if (opciones.ExportarExcel && opciones.AbrirAlTerminar)
            {
                dialogo.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir el archivo Excel");
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
