using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ExportacionMetrados.Core.Metrado;
using ExportacionMetrados.Core.Metrado.Encofrado;
using ExportacionMetrados.UI;

namespace ExportacionMetrados
{
    /// <summary>
    /// Comando "Metrado de encofrado" (panel Encofrado de la pestaña ARBA): calcula el encofrado de
    /// vigas, columnas, cimentaciones, losas y muros de concreto a partir de su geometría y de su
    /// contexto (<see cref="CalculadorEncofrado"/>), lo escribe en "Metrado - Encofrado (m²)", crea
    /// las tablas "Metrado encofrado - {elemento}" y exporta el detalle a Excel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class MetradoEncofradoCommand : IExternalCommand
    {
        private const string Titulo = "Metrado de encofrado";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show(Titulo, "Abra un proyecto de Revit antes de ejecutar el comando.");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;

            try
            {
                var ventana = new MetradoEncofradoWindow(SugerirNombreArchivo(doc));
                _ = new System.Windows.Interop.WindowInteropHelper(ventana)
                {
                    Owner = commandData.Application.MainWindowHandle
                };
                if (ventana.ShowDialog() != true) return Result.Cancelled;

                OpcionesEncofrado opciones = ventana.Opciones;
                var advertencias = new List<string>();

                if (opciones.CrearTablas &&
                    (ClasificadorElementos.IdParametroMaterial(doc) == null || ClasificadorElementos.IdParametroElementoRefuerzo(doc) == null))
                {
                    advertencias.Add("El proyecto aún no tiene \"" + ClasificadorElementos.NombreParametroMaterial + "\" o \"" +
                                     ClasificadorElementos.NombreParametroElementoRefuerzo + "\": las tablas de encofrado no se pueden filtrar por " +
                                     "grupo. Ejecute antes \"Metrado automático\" o \"Parámetros y filtros\" y vuelva a crear las tablas.");
                }

                // 1. Cálculo sobre la geometría (solo lectura, fuera de la transacción).
                ResultadoEncofrado resultado = new CalculadorEncofrado(doc, opciones).Calcular();
                advertencias.AddRange(resultado.Advertencias);

                if (resultado.Elementos.Count == 0)
                {
                    var aviso = new TaskDialog(Titulo)
                    {
                        MainInstruction = "No hay nada que metrar",
                        MainContent = "No se encontró ningún elemento de concreto en los tipos de elemento marcados " +
                                      $"(elementos de concreto en el modelo: {resultado.ElementosConcreto}). " +
                                      "Compruebe la clasificación \"Metrado - Material\" de los elementos (Metrado automático o Parámetros y filtros).",
                        CommonButtons = TaskDialogCommonButtons.Close,
                    };
                    if (advertencias.Count > 0) aviso.ExpandedContent = string.Join("\n", advertencias.Select(a => "  • " + a));
                    aviso.Show();
                    return Result.Cancelled;
                }

                // 2. Escritura en Revit: parámetro y tablas.
                int escritos = 0, subproyectos = 0, pieles = 0, pielSinDescuento = 0;
                bool parametroOk = true;
                var tablas = new List<ViewSchedule>();
                GeneradorTablasRevit generador = null;

                if (opciones.EscribirParametro || opciones.CrearTablas || opciones.CrearPiel)
                {
                    OpcionesMetrado opcionesMetrado = opciones.ComoOpcionesMetrado();
                    if (opciones.ReservarSubproyectos)
                    {
                        subproyectos = GestorSubproyectos.Reservar(doc, uidoc.ActiveView, opcionesMetrado, advertencias);
                    }

                    using (var t = new Transaction(doc, Titulo))
                    {
                        t.Start();
                        parametroOk = ParametroEncofrado.Asegurar(doc, advertencias);
                        doc.Regenerate();

                        if (parametroOk && opciones.EscribirParametro)
                        {
                            escritos = ParametroEncofrado.Escribir(doc, resultado.Elementos, advertencias);
                        }

                        if (opciones.CrearTablas)
                        {
                            generador = new GeneradorTablasRevit(doc, opcionesMetrado, uidoc.ActiveView?.Id);
                            tablas = generador.GenerarEncofrado(opciones.Reglas.Where(r => r.Seleccionada).Select(r => r.Grupo), opciones.TablaGeneral);
                            advertencias.AddRange(generador.Advertencias);
                        }

                        // Piel de verificación: reemplaza la del cálculo anterior.
                        if (opciones.CrearPiel)
                        {
                            pieles = PielEncofrado.Crear(doc, resultado.Elementos, advertencias, out pielSinDescuento);
                        }
                        t.Commit();
                    }
                }

                // 3. Excel.
                if (opciones.ExportarExcel)
                {
                    try { advertencias.AddRange(new ExportadorEncofrado(opciones).Exportar(resultado, doc.Title, tablas)); }
                    catch (Exception ex) { advertencias.Add("No se pudo escribir el archivo Excel: " + ex.Message); }
                }

                // 4. Abrir la primera tabla.
                if (opciones.CrearTablas && opciones.AbrirTablaAlTerminar && tablas.Count > 0)
                {
                    try { uidoc.ActiveView = tablas[0]; }
                    catch (Exception) { /* no es crítico */ }
                }

                MostrarResumen(resultado, opciones, tablas, generador, escritos, parametroOk, subproyectos, pieles, pielSinDescuento, advertencias);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Titulo, "Ocurrió un error durante el metrado de encofrado:\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static string SugerirNombreArchivo(Document doc)
        {
            string nombre = string.IsNullOrWhiteSpace(doc.Title) ? "Proyecto" : doc.Title;
            foreach (char c in Path.GetInvalidFileNameChars()) nombre = nombre.Replace(c, '_');
            return nombre + " - Metrado encofrado.xlsx";
        }

        private static void MostrarResumen(ResultadoEncofrado r, OpcionesEncofrado op, List<ViewSchedule> tablas, GeneradorTablasRevit generador,
            int escritos, bool parametroOk, int subproyectos, int pieles, int pielSinDescuento, List<string> advertencias)
        {
            var lineas = new List<string>
            {
                $"Elementos de concreto en el modelo (contexto): {r.ElementosConcreto}",
                $"Elementos metrados: {r.Elementos.Count}",
            };
            foreach (ReglaEncofrado regla in op.Reglas.Where(x => x.Seleccionada))
            {
                var del = r.Elementos.Where(e => e.Grupo == regla.Grupo.Nombre).ToList();
                lineas.Add($"    {regla.Grupo.Nombre}: {del.Count} elementos, {del.Sum(e => e.TotalM2):N2} m²");
            }
            lineas.Add(string.Empty);
            lineas.Add($"Encofrado total: {r.TotalM2:N2} m²  (laterales {r.LateralM2:N2} m², fondos {r.FondoM2:N2} m²)");
            lineas.Add(op.DescontarContactos
                ? $"Descontado por contacto con otros elementos de concreto: {r.DescuentoM2:N2} m² (tolerancia {op.ToleranciaContactoMm:0.#} mm)"
                : "Sin descontar contactos con otros elementos (opción desactivada)");
            int excluidos = r.Elementos.Count(e => e.FondoExcluido);
            if (excluidos > 0) lineas.Add($"Losas sin fondo contado (sobre terreno u opción): {excluidos}");
            if (r.Aproximados > 0) lineas.Add($"Elementos con caras curvas o cálculo aproximado: {r.Aproximados} (caras muestreadas: {r.CarasMuestreadas})");
            lineas.Add(string.Empty);
            if (op.EscribirParametro)
            {
                lineas.Add(parametroOk
                    ? $"\"{ParametroEncofrado.Nombre}\" actualizado en {escritos} elementos"
                    : $"No se pudo crear el parámetro \"{ParametroEncofrado.Nombre}\" (ver advertencias)");
            }
            if (generador != null)
            {
                lineas.Add($"Tablas creadas en Revit: {generador.TablasCreadas.Count}; reutilizadas: {generador.TablasReutilizadas.Count}");
            }
            if (op.CrearPiel)
            {
                lineas.Add($"Piel de encofrado de verificación: {pieles} elementos (modelos genéricos \"{PielEncofrado.PrefijoNombre}<grupo>\", " +
                           "un color por grupo; véala en una vista sombreada)" +
                           (pielSinDescuento > 0 ? $"; caras pintadas enteras, sin descuento (curvas o aproximadas): {pielSinDescuento}" : string.Empty));
            }
            if (subproyectos > 0) lineas.Add($"Subproyectos reservados (modelo compartido): {subproyectos}");
            lineas.Add($"Tiempo de cálculo: {r.Duracion.TotalSeconds:0.#} s ({r.OperacionesBooleanas} intersecciones de geometría)");
            if (op.ExportarExcel) lineas.Add("\nArchivo Excel:\n" + op.RutaArchivo);

            var dialogo = new TaskDialog(Titulo)
            {
                MainInstruction = advertencias.Count == 0 ? "Metrado de encofrado completado" : "Metrado de encofrado completado con advertencias",
                MainContent = string.Join("\n", lineas),
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
                if (detalle.Count > 0) detalle.Add(string.Empty);
                detalle.Add("Advertencias:");
                detalle.AddRange(advertencias.Select(a => "  • " + a));
            }
            if (detalle.Count > 0) dialogo.ExpandedContent = string.Join("\n", detalle);

            if (op.ExportarExcel && op.AbrirAlTerminar && File.Exists(op.RutaArchivo))
            {
                dialogo.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir el archivo Excel");
            }

            if (dialogo.Show() == TaskDialogResult.CommandLink1)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(op.RutaArchivo) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(Titulo, "No se pudo abrir el archivo:\n" + ex.Message);
                }
            }
        }
    }
}
