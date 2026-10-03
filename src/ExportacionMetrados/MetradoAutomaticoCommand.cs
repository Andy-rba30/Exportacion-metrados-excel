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
                GeneradorFiltrosVista filtros = null;
                // 0. Cálculo directo del modelo (resumen con m³ y kg; también alimenta el peso de las tablas).
                ResultadoMetrado resultado = new CalculadorMetrado(doc, opciones).Calcular();
                advertencias.AddRange(resultado.Advertencias);

                // 0b. Modelos compartidos: reservar los subproyectos antes de escribir (fuera de la transacción).
                int subproyectos = opciones.ReservarSubproyectos
                    ? GestorSubproyectos.Reservar(doc, uidoc.ActiveView, opciones, advertencias)
                    : 0;

                int clasificados = 0, particionados = 0, elementosRefuerzo = 0, elementosGrupo = 0, pesados = 0, perfilesPesados = 0;
                using (var t = new Transaction(doc, "Metrado automático"))
                {
                    t.Start();

                    // 1a. Parámetro "Metrado - Material" y clasificación concreto / metálico.
                    var categoriasSeleccionadas = opciones.Categorias.Where(c => c.Seleccionada).ToList();
                    var categoriasBic = categoriasSeleccionadas.SelectMany(c => c.Categorias).Distinct().ToList();
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

                    // 1b'. "Metrado - Elemento": en cada refuerzo el grupo de su anfitrión real (base de
                    //      los filtros y tablas de acero por elemento; no depende de las particiones) y en
                    //      cada elemento su propio grupo (VIGAS, ..., OTROS; filtra la tabla de "Otros").
                    if (ClasificadorElementos.AsegurarParametroElemento(doc, categoriasBic, advertencias))
                    {
                        doc.Regenerate();
                        elementosRefuerzo = ClasificadorElementos.RellenarElementoRefuerzo(doc, ClasificadorElementos.TodoElRefuerzo(doc),
                            opciones.Categorias, advertencias);
                        elementosGrupo = ClasificadorElementos.RellenarElementoEnElementos(doc, categoriasSeleccionadas, advertencias);
                    }

                    // 1c. Peso en kg: armaduras (longitud total × kg/m) y perfiles metálicos
                    //     (longitud × área de sección × densidad del acero al carbono).
                    bool necesitaPeso = opciones.IncluirAcero || opciones.TablasAceroEstructural || resultado.AceroEstructural.Count > 0;
                    if (necesitaPeso && ClasificadorElementos.AsegurarParametroPeso(doc, advertencias))
                    {
                        doc.Regenerate();
                        if (opciones.IncluirAcero) pesados = ClasificadorElementos.RellenarPesos(doc, resultado.Acero, advertencias);
                        perfilesPesados = ClasificadorElementos.RellenarPesosPerfiles(doc, resultado.AceroEstructural, advertencias);
                    }

                    // 1d. Tablas.
                    generador = new GeneradorTablasRevit(doc, opciones, uidoc.ActiveView?.Id);
                    tablas = generador.Generar();

                    // 1e. Filtros de vista por colores para comprobar el metrado (opcional).
                    if (opciones.CrearFiltrosVista)
                    {
                        filtros = new GeneradorFiltrosVista(doc, opciones);
                        filtros.Generar(uidoc.ActiveView);
                        advertencias.AddRange(filtros.Advertencias);
                    }
                    t.Commit();
                }
                advertencias.AddRange(generador.Advertencias);

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

                MostrarResumen(generador, filtros, tablas, resultado, opciones, advertencias, subproyectos, clasificados, particionados,
                    elementosRefuerzo, elementosGrupo, pesados, perfilesPesados);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // El error se muestra aquí; no se devuelve en "message" para que Revit
                // no lo repita en su diálogo "Error - cannot be ignored".
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

        private static void MostrarResumen(GeneradorTablasRevit generador, GeneradorFiltrosVista filtros, List<ViewSchedule> tablas,
            ResultadoMetrado resultado, OpcionesMetrado opciones, List<string> advertencias, int subproyectos, int clasificados,
            int particionados, int elementosRefuerzo, int elementosGrupo, int pesados, int perfilesPesados)
        {
            double m3 = resultado.Concreto.Sum(c => c.VolumenM3);
            double kg = resultado.Acero.Sum(a => a.PesoKg);
            double kgPerfiles = resultado.AceroEstructural.Sum(a => a.PesoKg);

            string contenido =
                (subproyectos > 0 ? $"Subproyectos reservados (modelo compartido): {subproyectos}\n" : string.Empty) +
                $"Tablas creadas en Revit: {generador.TablasCreadas.Count}\n" +
                $"Tablas existentes reutilizadas: {generador.TablasReutilizadas.Count}\n" +
                $"Elementos clasificados (Metrado - Material): {clasificados}\n" +
                $"Refuerzos con partición asignada: {particionados}\n" +
                $"Refuerzos con elemento anfitrión (Metrado - Elemento): {elementosRefuerzo}\n" +
                $"Elementos con grupo de metrado (Metrado - Elemento): {elementosGrupo}\n" +
                $"Refuerzos con peso actualizado: {pesados}\n" +
                $"Perfiles metálicos con peso actualizado: {perfilesPesados}\n" +
                (filtros != null
                    ? $"Filtros de vista por colores: {filtros.FiltrosCreados.Count} creados, {filtros.FiltrosReutilizados.Count} actualizados" +
                      (filtros.VistaAplicada != null ? $", aplicados a la vista \"{filtros.VistaAplicada}\"" : string.Empty) + "\n"
                    : string.Empty) +
                "\n" +
                $"Concreto: {resultado.Concreto.Count} elementos, {m3:N3} m³\n" +
                $"Acero estructural: {resultado.AceroEstructural.Count} perfiles, {kgPerfiles:N2} kg\n" +
                $"Acero de refuerzo: {resultado.Acero.Count} conjuntos de barras, {kg:N2} kg\n";

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
