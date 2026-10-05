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
        /// <summary>Contadores que el metrado escribe en el modelo, para el resumen final.</summary>
        private sealed class Contadores
        {
            public int Subproyectos, Clasificados, MaterialesRespetados, Particionados, ParticionesArba;
            public int ElementosRefuerzo, ElementosGrupo, Miscelaneos, Pesados, PerfilesPesados, PesosRespetados;
            public bool ParametrosOk;
        }

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
                var c = new Contadores();

                // 1. Tablas de planificación en Revit (requiere transacción).
                List<ViewSchedule> tablas;
                GeneradorTablasRevit generador;
                GeneradorFiltrosVista filtros = null;
                // 0. Cálculo directo del modelo (resumen con m³ y kg; también alimenta el peso de las tablas).
                ResultadoMetrado resultado = new CalculadorMetrado(doc, opciones).Calcular();
                advertencias.AddRange(resultado.Advertencias);

                // 0a. Vista para los filtros de colores: la activa o, si no los admite (la tabla que dejó
                //     abierta el metrado anterior, un plano, una plantilla que controla los filtros), otra
                //     vista gráfica abierta o la 3D predeterminada.
                View vistaFiltros = null;
                if (opciones.CrearFiltrosVista)
                {
                    vistaFiltros = GeneradorFiltrosVista.ElegirVista(VistasParaFiltros(uidoc), out string aviso);
                    if (aviso != null) advertencias.Add(aviso);
                }

                // 0b. Modelos compartidos: reservar los subproyectos antes de escribir (fuera de la transacción).
                c.Subproyectos = opciones.ReservarSubproyectos
                    ? GestorSubproyectos.Reservar(doc, vistaFiltros ?? uidoc.ActiveView, opciones, advertencias)
                    : 0;

                using (var t = new Transaction(doc, "Metrado automático"))
                {
                    t.Start();

                    // 1. Parámetros compartidos del contrato ARBA-comun (los ocho, con sus categorías):
                    //    si el proyecto tenía homónimos manuales se migran conservando los valores (avisa).
                    c.ParametrosOk = ClasificadorElementos.AsegurarParametrosContrato(doc, advertencias);
                    doc.Regenerate();

                    // 1a. Clasificación concreto / metálico en "Metrado - Material".
                    var categoriasSeleccionadas = opciones.Categorias.Where(x => x.Seleccionada).ToList();
                    // "Conexiones y anclajes" recoge por nombre piezas de cualquier categoría metálica:
                    // si está marcado, esas categorías también se clasifican y reciben el parámetro.
                    bool conexiones = categoriasSeleccionadas.Any(x => x.EsConexiones);
                    var categoriasBic = categoriasSeleccionadas
                        .Concat(conexiones ? opciones.Categorias.Where(x => x.PuedeSerMetalica) : Enumerable.Empty<CategoriaMetrado>())
                        .SelectMany(x => x.Categorias).Distinct().ToList();
                    if (ClasificadorElementos.IdParametroMaterial(doc) != null)
                    {
                        c.Clasificados = ClasificadorElementos.RellenarMaterial(doc, categoriasBic, opciones.ConservarClasificacionMaterial,
                            advertencias, out c.MaterialesRespetados);
                    }

                    // 1b. Partición del refuerzo sin origen ARBA: "CATEGORIA - MAN-marca" + ARBA - Origen = MANUAL.
                    if (opciones.IncluirAcero && opciones.RellenarParticiones)
                    {
                        c.Particionados = ClasificadorElementos.AsignarParticion(doc, ClasificadorElementos.TodoElRefuerzo(doc),
                            opciones.SobrescribirParticiones, null, advertencias, out c.ParticionesArba);
                    }

                    // 1b'. "Metrado - Elemento": en cada refuerzo el grupo de su anfitrión real (base de
                    //      los filtros y tablas de acero por elemento; no depende de las particiones) y en
                    //      cada elemento su propio grupo (VIGAS, ..., OTROS; MISCELANEOS si tiene partida).
                    if (ClasificadorElementos.IdParametroElementoRefuerzo(doc) != null)
                    {
                        c.ElementosRefuerzo = ClasificadorElementos.RellenarElementoRefuerzo(doc, ClasificadorElementos.TodoElRefuerzo(doc),
                            opciones.Categorias, advertencias);
                        c.ElementosGrupo = ClasificadorElementos.RellenarElementoEnElementos(doc, categoriasBic, opciones.Categorias,
                            advertencias, out c.Miscelaneos);
                    }

                    // 1c. Peso en kg: armaduras (longitud total × kg/m) y perfiles metálicos
                    //     (longitud × área de sección × densidad del acero al carbono). Los pesos
                    //     escritos por un add-in ARBA (rejillas, ángulos) se respetan.
                    bool necesitaPeso = opciones.IncluirAcero || opciones.TablasAceroEstructural || resultado.AceroEstructural.Count > 0;
                    if (necesitaPeso && ClasificadorElementos.IdParametroPeso(doc) != null)
                    {
                        int respetadosBarras = 0;
                        if (opciones.IncluirAcero) c.Pesados = ClasificadorElementos.RellenarPesos(doc, resultado.Acero, advertencias, out respetadosBarras);
                        c.PerfilesPesados = ClasificadorElementos.RellenarPesosPerfiles(doc, resultado.AceroEstructural, advertencias, out int respetadosPerfiles);
                        c.PesosRespetados = respetadosBarras + respetadosPerfiles;
                    }

                    // 1d. Tablas.
                    generador = new GeneradorTablasRevit(doc, opciones, uidoc.ActiveView?.Id);
                    tablas = generador.Generar();

                    // 1e. Filtros de vista por colores para comprobar el metrado (opcional).
                    if (opciones.CrearFiltrosVista)
                    {
                        filtros = new GeneradorFiltrosVista(doc, opciones);
                        filtros.Generar(vistaFiltros);
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

                MostrarResumen(generador, filtros, tablas, resultado, opciones, advertencias, c);
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

        /// <summary>
        /// Vistas en las que se pueden aplicar los filtros, por orden de preferencia: la activa, las
        /// demás vistas abiertas (las 3D primero) y la 3D predeterminada ("{3D}", o "{3D - usuario}"
        /// en un modelo compartido), aunque no esté abierta.
        /// </summary>
        internal static List<View> VistasParaFiltros(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            var vistas = new List<View> { uidoc.ActiveView };

            var abiertas = new List<View>();
            try
            {
                foreach (UIView uiView in uidoc.GetOpenUIViews())
                {
                    if (doc.GetElement(uiView.ViewId) is View v) abiertas.Add(v);
                }
            }
            catch (Exception) { /* sin vistas abiertas: se prueba la 3D predeterminada */ }
            vistas.AddRange(abiertas.OrderBy(v => v is View3D ? 0 : 1));

            string usuario = string.Empty;
            try { usuario = doc.Application.Username ?? string.Empty; }
            catch (Exception) { }
            var nombres3D = new[] { "{3D}", "{3D - " + usuario + "}" };
            vistas.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate && nombres3D.Contains(v.Name))
                .OrderBy(v => Array.IndexOf(nombres3D, v.Name)));

            return vistas.Where(v => v != null).GroupBy(v => v.Id).Select(g => g.First()).ToList();
        }

        private static string SugerirNombreArchivo(Document doc)
        {
            string nombre = string.IsNullOrWhiteSpace(doc.Title) ? "Proyecto" : doc.Title;
            foreach (char c in Path.GetInvalidFileNameChars()) nombre = nombre.Replace(c, '_');
            return nombre + " - Metrado concreto y acero.xlsx";
        }

        private static void MostrarResumen(GeneradorTablasRevit generador, GeneradorFiltrosVista filtros, List<ViewSchedule> tablas,
            ResultadoMetrado resultado, OpcionesMetrado opciones, List<string> advertencias, Contadores c)
        {
            double m3 = resultado.Concreto.Sum(x => x.VolumenM3);
            double kg = resultado.Acero.Sum(a => a.PesoKg);
            double kgPerfiles = resultado.AceroEstructural.Where(a => !a.EsMiscelaneo).Sum(a => a.PesoKg);
            var miscelaneos = resultado.AceroEstructural.Where(a => a.EsMiscelaneo).ToList();
            int miscelaneosEnModelo = Math.Max(c.Miscelaneos, miscelaneos.Count);
            int conPesoRevit = resultado.AceroEstructural.Count(a => !a.EsMiscelaneo && a.PesoDeRevit);

            string contenido =
                $"Contrato ARBA-comun: {ClasificadorElementos.VersionContrato}" +
                (c.ParametrosOk ? string.Empty : " (no se pudieron asegurar todos sus parámetros; ver advertencias)") + "\n" +
                (c.Subproyectos > 0 ? $"Subproyectos reservados (modelo compartido): {c.Subproyectos}\n" : string.Empty) +
                $"Tablas creadas en Revit: {generador.TablasCreadas.Count}\n" +
                $"Tablas existentes reutilizadas: {generador.TablasReutilizadas.Count}\n" +
                $"Elementos clasificados (Metrado - Material): {c.Clasificados}" +
                (c.MaterialesRespetados > 0 ? $" (respetados de add-ins ARBA: {c.MaterialesRespetados})" : string.Empty) + "\n" +
                $"Refuerzos con partición asignada (CATEGORIA - MAN-marca): {c.Particionados}\n" +
                $"Particiones de add-ins ARBA respetadas: {c.ParticionesArba}\n" +
                $"Refuerzos con elemento anfitrión (Metrado - Elemento): {c.ElementosRefuerzo}\n" +
                $"Elementos con grupo de metrado (Metrado - Elemento): {c.ElementosGrupo}\n" +
                $"Misceláneos (con Metrado - Partida): {miscelaneosEnModelo}\n" +
                $"Refuerzos con peso actualizado: {c.Pesados}\n" +
                $"Perfiles y piezas metálicas con peso actualizado: {c.PerfilesPesados}\n" +
                $"Pesos escritos por add-ins ARBA respetados: {c.PesosRespetados}\n" +
                (filtros != null
                    ? $"Filtros de vista por colores: {filtros.FiltrosCreados.Count} creados, {filtros.FiltrosReutilizados.Count} actualizados" +
                      (filtros.VistaAplicada != null ? $", aplicados a la vista \"{filtros.VistaAplicada}\"" : string.Empty) + "\n"
                    : string.Empty) +
                "\n" +
                $"Concreto: {resultado.Concreto.Count} elementos, {m3:N3} m³\n" +
                $"Acero estructural: {resultado.AceroEstructural.Count - miscelaneos.Count} perfiles, {kgPerfiles:N2} kg" +
                (conPesoRevit > 0 ? $" ({conPesoRevit} con el peso que ya trae Revit)" : string.Empty) + "\n" +
                (miscelaneos.Count > 0
                    ? $"Misceláneos: {miscelaneos.Count} piezas, {miscelaneos.Sum(a => a.PesoKg):N2} kg, {miscelaneos.Sum(a => a.Pernos)} pernos, " +
                      $"{miscelaneos.Select(a => a.Partida).Distinct().Count()} partida(s)\n"
                    : string.Empty) +
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
