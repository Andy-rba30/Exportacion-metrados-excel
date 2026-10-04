using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using ClosedXML.Excel;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Escribe el resultado del metrado automático en un libro de Excel con las
    /// hojas: Resumen, Concreto, Acero estructural, Acero y (opcional) detalle por elemento.
    /// </summary>
    public class ExportadorMetrado
    {
        private static readonly XLColor ColorTitulo = XLColor.FromArgb(0x1F, 0x4E, 0x78);
        private static readonly XLColor ColorEncabezado = XLColor.FromArgb(0xD9, 0xE1, 0xF2);
        private static readonly XLColor ColorGrupo = XLColor.FromArgb(0xF2, 0xF2, 0xF2);
        private static readonly XLColor ColorSubtotal = XLColor.FromArgb(0xFF, 0xF2, 0xCC);
        private static readonly XLColor ColorTotal = XLColor.FromArgb(0xFC, 0xE4, 0xD6);

        private const string FormatoM3 = "#,##0.000";
        private const string FormatoM = "#,##0.00";
        private const string FormatoKg = "#,##0.00";
        private const string FormatoEntero = "#,##0";

        private readonly OpcionesMetrado _opciones;

        public ExportadorMetrado(OpcionesMetrado opciones)
        {
            _opciones = opciones ?? throw new ArgumentNullException(nameof(opciones));
        }

        /// <summary>
        /// Escribe el libro. Si se pasan tablas de Revit, cada una va en su propia hoja
        /// (tal como se ve en Revit); si no, se escriben las hojas calculadas Concreto y Acero.
        /// </summary>
        public List<string> Exportar(ResultadoMetrado resultado, string tituloProyecto, IList<ViewSchedule> tablasRevit = null)
        {
            string ruta = _opciones.RutaArchivo;
            string carpeta = Path.GetDirectoryName(ruta);
            if (!string.IsNullOrEmpty(carpeta)) Directory.CreateDirectory(carpeta);

            var ordenCategorias = _opciones.Categorias.Where(c => c.Seleccionada).Select(c => c.Nombre).ToList();
            var errores = new List<string>();

            using (var libro = new XLWorkbook())
            {
                EscribirResumen(libro.Worksheets.Add("Resumen"), resultado, ordenCategorias, tituloProyecto);

                if (tablasRevit != null && tablasRevit.Count > 0)
                {
                    var exportadorTablas = new ExportadorExcel(new OpcionesExportacion());
                    var nombresUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Resumen" };
                    foreach (ViewSchedule tabla in tablasRevit)
                    {
                        try { exportadorTablas.AgregarHoja(libro, tabla, nombresUsados); }
                        catch (Exception ex) { errores.Add($"Tabla \"{tabla.Name}\": {ex.Message}"); }
                    }
                }
                else
                {
                    EscribirConcreto(libro.Worksheets.Add("Concreto"), resultado, ordenCategorias);
                    if (resultado.AceroEstructural.Count > 0)
                    {
                        EscribirAceroEstructural(libro.Worksheets.Add("Acero estructural"), resultado, ordenCategorias);
                    }
                    if (_opciones.IncluirAcero)
                    {
                        EscribirAcero(libro.Worksheets.Add("Acero"), resultado, ordenCategorias);
                    }
                }

                if (_opciones.IncluirDetalle)
                {
                    EscribirDetalleConcreto(libro.Worksheets.Add("Concreto - Detalle"), resultado);
                    if (resultado.AceroEstructural.Count > 0)
                    {
                        EscribirDetalleAceroEstructural(libro.Worksheets.Add("Acero estructural - Detalle"), resultado);
                    }
                    if (_opciones.IncluirAcero)
                    {
                        EscribirDetalleAcero(libro.Worksheets.Add("Acero - Detalle"), resultado);
                    }
                }

                ExportadorExcel.GuardarLibro(libro, ruta);
            }

            return errores;
        }

        // ------------------------------------------------------------------
        // Resumen
        // ------------------------------------------------------------------

        private void EscribirResumen(IXLWorksheet hoja, ResultadoMetrado r, List<string> categorias, string proyecto)
        {
            int fila = 1;
            Titulo(hoja, fila++, 5, "RESUMEN DE METRADOS - " + proyecto);
            hoja.Cell(fila++, 1).Value = "Generado: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
            fila++;

            Encabezado(hoja, fila++, "Elemento", "Concreto (m³)", "Acero (kg)", "Acero (kg/m³)", "N° elementos");
            int primera = fila;

            foreach (string cat in categorias)
            {
                double m3 = r.Concreto.Where(c => c.Categoria == cat).Sum(c => c.VolumenM3);
                double kg = r.Acero.Where(a => a.CategoriaHost == cat).Sum(a => a.PesoKg);
                int n = r.Concreto.Count(c => c.Categoria == cat);

                hoja.Cell(fila, 1).Value = cat;
                Numero(hoja.Cell(fila, 2), m3, FormatoM3);
                Numero(hoja.Cell(fila, 3), kg, FormatoKg);
                hoja.Cell(fila, 4).FormulaA1 = $"IF(B{fila}=0,0,C{fila}/B{fila})";
                hoja.Cell(fila, 4).Style.NumberFormat.Format = FormatoKg;
                Numero(hoja.Cell(fila, 5), n, FormatoEntero);
                fila++;
            }

            // Acero alojado en elementos de categorías no marcadas (p. ej. muros).
            foreach (var g in r.Acero.Where(a => !categorias.Contains(a.CategoriaHost))
                                     .GroupBy(a => a.CategoriaHost).OrderBy(g => g.Key))
            {
                hoja.Cell(fila, 1).Value = g.Key + " (solo acero)";
                Numero(hoja.Cell(fila, 2), 0, FormatoM3);
                Numero(hoja.Cell(fila, 3), g.Sum(a => a.PesoKg), FormatoKg);
                Numero(hoja.Cell(fila, 5), 0, FormatoEntero);
                fila++;
            }

            int ultima = fila - 1;
            hoja.Cell(fila, 1).Value = "TOTAL";
            hoja.Cell(fila, 2).FormulaA1 = $"SUM(B{primera}:B{ultima})";
            hoja.Cell(fila, 3).FormulaA1 = $"SUM(C{primera}:C{ultima})";
            hoja.Cell(fila, 4).FormulaA1 = $"IF(B{fila}=0,0,C{fila}/B{fila})";
            hoja.Cell(fila, 5).FormulaA1 = $"SUM(E{primera}:E{ultima})";
            hoja.Cell(fila, 2).Style.NumberFormat.Format = FormatoM3;
            hoja.Cell(fila, 3).Style.NumberFormat.Format = FormatoKg;
            hoja.Cell(fila, 4).Style.NumberFormat.Format = FormatoKg;
            hoja.Cell(fila, 5).Style.NumberFormat.Format = FormatoEntero;
            FilaResaltada(hoja, fila, 5, ColorTotal, true);
            Bordes(hoja.Range(primera - 1, 1, fila, 5));
            fila += 2;

            // Perfiles metálicos: por peso, no por volumen.
            if (r.AceroEstructural.Count > 0)
            {
                Subtitulo(hoja, fila++, 5, "Acero estructural (perfiles y piezas metálicas)");
                hoja.Cell(fila++, 1).Value = NotaPesoAceroEstructural();
                Encabezado(hoja, fila++, "Elemento", "Longitud (m)", "Peso (kg)", "N° elementos", "");
                int p = fila;
                var ordenPerfiles = r.AceroEstructural
                    .GroupBy(a => a.Categoria)
                    .OrderBy(g => { int i = categorias.IndexOf(g.Key); return i < 0 ? int.MaxValue : i; })
                    .ThenBy(g => g.Key);
                foreach (var g in ordenPerfiles)
                {
                    hoja.Cell(fila, 1).Value = g.Key;
                    Numero(hoja.Cell(fila, 2), g.Sum(a => a.LongitudM), FormatoM);
                    Numero(hoja.Cell(fila, 3), g.Sum(a => a.PesoKg), FormatoKg);
                    Numero(hoja.Cell(fila, 4), g.Count(), FormatoEntero);
                    fila++;
                }
                hoja.Cell(fila, 1).Value = "TOTAL";
                hoja.Cell(fila, 2).FormulaA1 = $"SUM(B{p}:B{fila - 1})";
                hoja.Cell(fila, 3).FormulaA1 = $"SUM(C{p}:C{fila - 1})";
                hoja.Cell(fila, 4).FormulaA1 = $"SUM(D{p}:D{fila - 1})";
                hoja.Cell(fila, 2).Style.NumberFormat.Format = FormatoM;
                hoja.Cell(fila, 3).Style.NumberFormat.Format = FormatoKg;
                hoja.Cell(fila, 4).Style.NumberFormat.Format = FormatoEntero;
                FilaResaltada(hoja, fila, 4, ColorTotal, true);
                Bordes(hoja.Range(p - 1, 1, fila, 4));
                fila += 2;
            }

            // Misceláneos (contrato ARBA-comun): elementos con "Metrado - Partida", por partida.
            var miscelaneos = r.AceroEstructural.Where(a => a.EsMiscelaneo).ToList();
            if (miscelaneos.Count > 0)
            {
                Subtitulo(hoja, fila++, 5, "Misceláneos por partida (contrato ARBA-comun " + ClasificadorElementos.VersionContrato + ")");
                hoja.Cell(fila++, 1).Value = "Elementos con \"Metrado - Partida\" (rejillas, ángulos...). Se respeta el peso escrito por su add-in; " +
                                             "sin él, volumen × densidad.";
                Encabezado(hoja, fila++, "Partida", "Peso (kg)", "Pernos (und)", "N° piezas", "Piezas con peso de su add-in");
                int p = fila;
                foreach (var g in miscelaneos
                    .GroupBy(a => string.IsNullOrWhiteSpace(a.Partida) ? "(sin partida)" : a.Partida)
                    .OrderBy(g => g.Key))
                {
                    hoja.Cell(fila, 1).Value = g.Key;
                    Numero(hoja.Cell(fila, 2), g.Sum(a => a.PesoKg), FormatoKg);
                    Numero(hoja.Cell(fila, 3), g.Sum(a => a.Pernos), FormatoEntero);
                    Numero(hoja.Cell(fila, 4), g.Count(), FormatoEntero);
                    Numero(hoja.Cell(fila, 5), g.Count(a => a.PesoProtegido), FormatoEntero);
                    fila++;
                }
                hoja.Cell(fila, 1).Value = "TOTAL";
                hoja.Cell(fila, 2).FormulaA1 = $"SUM(B{p}:B{fila - 1})";
                hoja.Cell(fila, 3).FormulaA1 = $"SUM(C{p}:C{fila - 1})";
                hoja.Cell(fila, 4).FormulaA1 = $"SUM(D{p}:D{fila - 1})";
                hoja.Cell(fila, 5).FormulaA1 = $"SUM(E{p}:E{fila - 1})";
                hoja.Cell(fila, 2).Style.NumberFormat.Format = FormatoKg;
                hoja.Cell(fila, 3).Style.NumberFormat.Format = FormatoEntero;
                hoja.Cell(fila, 4).Style.NumberFormat.Format = FormatoEntero;
                hoja.Cell(fila, 5).Style.NumberFormat.Format = FormatoEntero;
                FilaResaltada(hoja, fila, 5, ColorTotal, true);
                Bordes(hoja.Range(p - 1, 1, fila, 5));
                fila += 2;
            }

            // Resumen de acero por diámetro
            if (_opciones.IncluirAcero && r.Acero.Count > 0)
            {
                Subtitulo(hoja, fila++, 5, "Acero por diámetro");
                Encabezado(hoja, fila++, "Diámetro (mm)", "Longitud (m)", "Peso (kg)", "", "");
                int p = fila;
                foreach (var g in r.Acero.GroupBy(a => Math.Round(a.DiametroMm, 2)).OrderBy(g => g.Key))
                {
                    if (g.Key > 0) Numero(hoja.Cell(fila, 1), g.Key, "0.##");
                    else hoja.Cell(fila, 1).Value = "Malla";
                    Numero(hoja.Cell(fila, 2), g.Sum(a => a.LongitudTotalM), FormatoM);
                    Numero(hoja.Cell(fila, 3), g.Sum(a => a.PesoKg), FormatoKg);
                    fila++;
                }
                hoja.Cell(fila, 1).Value = "TOTAL";
                hoja.Cell(fila, 2).FormulaA1 = $"SUM(B{p}:B{fila - 1})";
                hoja.Cell(fila, 3).FormulaA1 = $"SUM(C{p}:C{fila - 1})";
                hoja.Cell(fila, 2).Style.NumberFormat.Format = FormatoM;
                hoja.Cell(fila, 3).Style.NumberFormat.Format = FormatoKg;
                FilaResaltada(hoja, fila, 3, ColorTotal, true);
                Bordes(hoja.Range(p - 1, 1, fila, 3));
                fila += 2;
            }

            if (r.Advertencias.Count > 0)
            {
                Subtitulo(hoja, fila++, 5, "Advertencias");
                foreach (string adv in r.Advertencias)
                {
                    hoja.Cell(fila++, 1).SetValue(adv);
                }
            }

            hoja.Column(1).Width = 28;
            hoja.Columns(2, 5).Width = 16;
        }

        // ------------------------------------------------------------------
        // Concreto
        // ------------------------------------------------------------------

        private void EscribirConcreto(IXLWorksheet hoja, ResultadoMetrado r, List<string> categorias)
        {
            int fila = 1;
            const int nCol = 9;
            Titulo(hoja, fila++, nCol, "METRADO DE CONCRETO");
            fila++;

            string[] columnas = { "Elemento", "Nivel", "Tipo", "Material", "Cantidad", "Longitud / altura (m)", "Área (m²)", "Espesor (m)", "Volumen (m³)" };
            var filasSubtotal = new List<int>();

            foreach (string cat in categorias)
            {
                var elementos = r.Concreto.Where(c => c.Categoria == cat).ToList();

                Subtitulo(hoja, fila++, nCol, cat.ToUpperInvariant());
                Encabezado(hoja, fila++, columnas);

                if (elementos.Count == 0)
                {
                    hoja.Cell(fila++, 1).Value = "(sin elementos)";
                    fila++;
                    continue;
                }

                int primera = fila;
                bool porNiveles = AgruparPorNivel(cat);
                var porNivel = elementos
                    .GroupBy(e => new { Nivel = porNiveles ? e.Nivel : string.Empty, ElevacionNivel = porNiveles ? e.ElevacionNivel : 0.0 })
                    .OrderBy(g => g.Key.ElevacionNivel)
                    .ThenBy(g => g.Key.Nivel);

                foreach (var nivel in porNivel)
                {
                    var porTipo = nivel
                        .GroupBy(e => new { e.Familia, e.Tipo, e.Material })
                        .OrderBy(g => g.Key.Familia).ThenBy(g => g.Key.Tipo);

                    foreach (var tipo in porTipo)
                    {
                        hoja.Cell(fila, 1).Value = cat;
                        hoja.Cell(fila, 2).Value = nivel.Key.Nivel;
                        hoja.Cell(fila, 3).Value = NombreTipo(tipo.Key.Familia, tipo.Key.Tipo);
                        hoja.Cell(fila, 4).Value = tipo.Key.Material;
                        Numero(hoja.Cell(fila, 5), tipo.Count(), FormatoEntero);
                        Numero(hoja.Cell(fila, 6), tipo.Sum(e => e.LongitudM), FormatoM);
                        Numero(hoja.Cell(fila, 7), tipo.Sum(e => e.AreaM2), FormatoM);
                        // El espesor es propio del tipo, no se suma: se muestra el del grupo.
                        double espesor = tipo.Max(e => e.EspesorM);
                        if (espesor > 0) Numero(hoja.Cell(fila, 8), espesor, FormatoM3);
                        Numero(hoja.Cell(fila, 9), tipo.Sum(e => e.VolumenM3), FormatoM3);
                        fila++;
                    }
                }

                int ultima = fila - 1;
                hoja.Cell(fila, 1).Value = "Subtotal " + cat;
                hoja.Cell(fila, 5).FormulaA1 = $"SUM(E{primera}:E{ultima})";
                hoja.Cell(fila, 6).FormulaA1 = $"SUM(F{primera}:F{ultima})";
                hoja.Cell(fila, 7).FormulaA1 = $"SUM(G{primera}:G{ultima})";
                hoja.Cell(fila, 9).FormulaA1 = $"SUM(I{primera}:I{ultima})";
                hoja.Cell(fila, 5).Style.NumberFormat.Format = FormatoEntero;
                hoja.Cell(fila, 6).Style.NumberFormat.Format = FormatoM;
                hoja.Cell(fila, 7).Style.NumberFormat.Format = FormatoM;
                hoja.Cell(fila, 9).Style.NumberFormat.Format = FormatoM3;
                FilaResaltada(hoja, fila, nCol, ColorSubtotal, true);
                Bordes(hoja.Range(primera - 1, 1, fila, nCol));
                filasSubtotal.Add(fila);
                fila += 2;
            }

            if (filasSubtotal.Count > 0)
            {
                hoja.Cell(fila, 1).Value = "TOTAL CONCRETO";
                hoja.Cell(fila, 9).FormulaA1 = string.Join("+", filasSubtotal.Select(f => $"I{f}"));
                hoja.Cell(fila, 9).Style.NumberFormat.Format = FormatoM3;
                FilaResaltada(hoja, fila, nCol, ColorTotal, true);
                Bordes(hoja.Range(fila, 1, fila, nCol));
            }

            AjustarColumnas(hoja, nCol);
        }

        // ------------------------------------------------------------------
        // Acero estructural (perfiles metálicos)
        // ------------------------------------------------------------------

        private void EscribirAceroEstructural(IXLWorksheet hoja, ResultadoMetrado r, List<string> categorias)
        {
            int fila = 1;
            const int nCol = 10;
            Titulo(hoja, fila++, nCol, "METRADO DE ACERO ESTRUCTURAL (PERFILES Y PIEZAS METÁLICAS)");
            hoja.Cell(fila++, 1).Value = NotaPesoAceroEstructural();
            fila++;

            string[] columnas =
            {
                "Elemento", "Nivel", "Tipo", "Material", "Cantidad", "Longitud (m)", "Área de sección (cm²)", "Volumen (m³)",
                "Densidad (kg/m³)", "Peso (kg)",
            };
            var filasSubtotal = new List<int>();

            var ordenCategorias = r.AceroEstructural.Select(a => a.Categoria).Distinct()
                .OrderBy(c => { int i = categorias.IndexOf(c); return i < 0 ? int.MaxValue : i; }).ThenBy(c => c).ToList();

            foreach (string cat in ordenCategorias)
            {
                var perfiles = r.AceroEstructural.Where(a => a.Categoria == cat).ToList();

                Subtitulo(hoja, fila++, nCol, cat.ToUpperInvariant());
                Encabezado(hoja, fila++, columnas);

                int primera = fila;
                bool porNiveles = AgruparPorNivel(cat);
                var porNivel = perfiles
                    .GroupBy(e => new { Nivel = porNiveles ? e.Nivel : string.Empty, ElevacionNivel = porNiveles ? e.ElevacionNivel : 0.0 })
                    .OrderBy(g => g.Key.ElevacionNivel)
                    .ThenBy(g => g.Key.Nivel);

                foreach (var nivel in porNivel)
                {
                    var porTipo = nivel
                        .GroupBy(e => new { e.Familia, e.Tipo, e.Material })
                        .OrderBy(g => g.Key.Familia).ThenBy(g => g.Key.Tipo);

                    foreach (var tipo in porTipo)
                    {
                        hoja.Cell(fila, 1).Value = cat;
                        hoja.Cell(fila, 2).Value = nivel.Key.Nivel;
                        hoja.Cell(fila, 3).Value = NombreTipo(tipo.Key.Familia, tipo.Key.Tipo);
                        hoja.Cell(fila, 4).Value = tipo.Key.Material;
                        Numero(hoja.Cell(fila, 5), tipo.Count(), FormatoEntero);
                        Numero(hoja.Cell(fila, 6), tipo.Sum(e => e.LongitudM), FormatoM);
                        // El área de sección es propia del tipo, no se suma: se muestra la del grupo.
                        Numero(hoja.Cell(fila, 7), tipo.Max(e => e.AreaSeccionCm2), FormatoM);
                        Numero(hoja.Cell(fila, 8), tipo.Sum(e => e.VolumenM3), FormatoM3);
                        Numero(hoja.Cell(fila, 9), tipo.Max(e => e.DensidadKgM3), FormatoEntero);
                        Numero(hoja.Cell(fila, 10), tipo.Sum(e => e.PesoKg), FormatoKg);
                        fila++;
                    }
                }

                int ultima = fila - 1;
                hoja.Cell(fila, 1).Value = "Subtotal " + cat;
                hoja.Cell(fila, 5).FormulaA1 = $"SUM(E{primera}:E{ultima})";
                hoja.Cell(fila, 6).FormulaA1 = $"SUM(F{primera}:F{ultima})";
                hoja.Cell(fila, 8).FormulaA1 = $"SUM(H{primera}:H{ultima})";
                hoja.Cell(fila, 10).FormulaA1 = $"SUM(J{primera}:J{ultima})";
                hoja.Cell(fila, 5).Style.NumberFormat.Format = FormatoEntero;
                hoja.Cell(fila, 6).Style.NumberFormat.Format = FormatoM;
                hoja.Cell(fila, 8).Style.NumberFormat.Format = FormatoM3;
                hoja.Cell(fila, 10).Style.NumberFormat.Format = FormatoKg;
                FilaResaltada(hoja, fila, nCol, ColorSubtotal, true);
                Bordes(hoja.Range(primera - 1, 1, fila, nCol));
                filasSubtotal.Add(fila);
                fila += 2;
            }

            if (filasSubtotal.Count > 0)
            {
                hoja.Cell(fila, 1).Value = "TOTAL ACERO ESTRUCTURAL";
                hoja.Cell(fila, 6).FormulaA1 = string.Join("+", filasSubtotal.Select(f => $"F{f}"));
                hoja.Cell(fila, 8).FormulaA1 = string.Join("+", filasSubtotal.Select(f => $"H{f}"));
                hoja.Cell(fila, 10).FormulaA1 = string.Join("+", filasSubtotal.Select(f => $"J{f}"));
                hoja.Cell(fila, 6).Style.NumberFormat.Format = FormatoM;
                hoja.Cell(fila, 8).Style.NumberFormat.Format = FormatoM3;
                hoja.Cell(fila, 10).Style.NumberFormat.Format = FormatoKg;
                FilaResaltada(hoja, fila, nCol, ColorTotal, true);
                Bordes(hoja.Range(fila, 1, fila, nCol));
            }

            AjustarColumnas(hoja, nCol);
        }

        private string NotaPesoAceroEstructural() =>
            $"Perfiles: peso = longitud × área de sección × densidad del acero al carbono ({_opciones.DensidadAceroEstructural:0} kg/m³); " +
            "los de las familias de acero de Revit que ya traen su peso (Exact Weight) usan ese peso. " +
            "Conexiones, planchas y piezas sin longitud: volumen × densidad.";

        // ------------------------------------------------------------------
        // Acero
        // ------------------------------------------------------------------

        private void EscribirAcero(IXLWorksheet hoja, ResultadoMetrado r, List<string> categorias)
        {
            int fila = 1;
            Titulo(hoja, fila++, 7, "METRADO DE ACERO DE REFUERZO");
            hoja.Cell(fila++, 1).Value =
                $"Peso calculado con densidad {_opciones.DensidadAcero:0} kg/m³ (o el parámetro de peso unitario del tipo de barra si existe).";
            fila++;

            string[] columnas = { "Elemento", "Nivel", "Tipo de barra", "Diámetro (mm)", "N° barras", "Longitud (m)", "Peso (kg)" };
            var filasSubtotal = new List<int>();

            foreach (string cat in categorias)
            {
                var barras = r.Acero.Where(a => a.CategoriaHost == cat).ToList();

                Subtitulo(hoja, fila++, 7, cat.ToUpperInvariant());
                Encabezado(hoja, fila++, columnas);

                if (barras.Count == 0)
                {
                    hoja.Cell(fila++, 1).Value = "(sin acero)";
                    fila++;
                    continue;
                }

                int primera = fila;
                var porNivel = barras
                    .GroupBy(a => new { a.Nivel, a.ElevacionNivel })
                    .OrderBy(g => g.Key.ElevacionNivel)
                    .ThenBy(g => g.Key.Nivel);

                foreach (var nivel in porNivel)
                {
                    var porDiametro = nivel
                        .GroupBy(a => new { Diametro = Math.Round(a.DiametroMm, 2), a.TipoBarra })
                        .OrderBy(g => g.Key.Diametro).ThenBy(g => g.Key.TipoBarra);

                    foreach (var d in porDiametro)
                    {
                        hoja.Cell(fila, 1).Value = cat;
                        hoja.Cell(fila, 2).Value = nivel.Key.Nivel;
                        hoja.Cell(fila, 3).Value = d.Key.TipoBarra;
                        if (d.Key.Diametro > 0) Numero(hoja.Cell(fila, 4), d.Key.Diametro, "0.##");
                        else hoja.Cell(fila, 4).Value = "Malla";
                        Numero(hoja.Cell(fila, 5), d.Sum(a => a.Cantidad), FormatoEntero);
                        Numero(hoja.Cell(fila, 6), d.Sum(a => a.LongitudTotalM), FormatoM);
                        Numero(hoja.Cell(fila, 7), d.Sum(a => a.PesoKg), FormatoKg);
                        fila++;
                    }
                }

                int ultima = fila - 1;
                hoja.Cell(fila, 1).Value = "Subtotal " + cat;
                hoja.Cell(fila, 5).FormulaA1 = $"SUM(E{primera}:E{ultima})";
                hoja.Cell(fila, 6).FormulaA1 = $"SUM(F{primera}:F{ultima})";
                hoja.Cell(fila, 7).FormulaA1 = $"SUM(G{primera}:G{ultima})";
                hoja.Cell(fila, 5).Style.NumberFormat.Format = FormatoEntero;
                hoja.Cell(fila, 6).Style.NumberFormat.Format = FormatoM;
                hoja.Cell(fila, 7).Style.NumberFormat.Format = FormatoKg;
                FilaResaltada(hoja, fila, 7, ColorSubtotal, true);
                Bordes(hoja.Range(primera - 1, 1, fila, 7));
                filasSubtotal.Add(fila);
                fila += 2;
            }

            if (filasSubtotal.Count > 0)
            {
                hoja.Cell(fila, 1).Value = "TOTAL ACERO";
                hoja.Cell(fila, 6).FormulaA1 = string.Join("+", filasSubtotal.Select(f => $"F{f}"));
                hoja.Cell(fila, 7).FormulaA1 = string.Join("+", filasSubtotal.Select(f => $"G{f}"));
                hoja.Cell(fila, 6).Style.NumberFormat.Format = FormatoM;
                hoja.Cell(fila, 7).Style.NumberFormat.Format = FormatoKg;
                FilaResaltada(hoja, fila, 7, ColorTotal, true);
                Bordes(hoja.Range(fila, 1, fila, 7));
            }

            AjustarColumnas(hoja, 7);
        }

        // ------------------------------------------------------------------
        // Detalle por elemento
        // ------------------------------------------------------------------

        private static void EscribirDetalleConcreto(IXLWorksheet hoja, ResultadoMetrado r)
        {
            int fila = 1;
            Encabezado(hoja, fila++, "Id", "Elemento", "Nivel", "Familia", "Tipo", "Marca", "Material", "Longitud / altura (m)", "Área (m²)", "Espesor (m)", "Volumen (m³)");

            foreach (var e in r.Concreto
                .OrderBy(c => c.Categoria).ThenBy(c => c.ElevacionNivel).ThenBy(c => c.Familia).ThenBy(c => c.Tipo))
            {
                Numero(hoja.Cell(fila, 1), IdNumerico(e.Id), "0");
                hoja.Cell(fila, 2).Value = e.Categoria;
                hoja.Cell(fila, 3).Value = e.Nivel;
                hoja.Cell(fila, 4).Value = e.Familia;
                hoja.Cell(fila, 5).Value = e.Tipo;
                hoja.Cell(fila, 6).SetValue(e.Marca ?? string.Empty);
                hoja.Cell(fila, 7).Value = e.Material;
                Numero(hoja.Cell(fila, 8), e.LongitudM, FormatoM);
                Numero(hoja.Cell(fila, 9), e.AreaM2, FormatoM);
                if (e.EspesorM > 0) Numero(hoja.Cell(fila, 10), e.EspesorM, FormatoM3);
                Numero(hoja.Cell(fila, 11), e.VolumenM3, FormatoM3);
                fila++;
            }

            if (fila > 2)
            {
                var rango = hoja.Range(1, 1, fila - 1, 11);
                rango.SetAutoFilter();
                Bordes(rango);
            }
            hoja.SheetView.FreezeRows(1);
            AjustarColumnas(hoja, 11);
        }

        private static void EscribirDetalleAceroEstructural(IXLWorksheet hoja, ResultadoMetrado r)
        {
            const int nCol = 17;
            int fila = 1;
            Encabezado(hoja, fila++, "Id", "Elemento", "Nivel", "Familia", "Tipo", "Marca", "Material", "Longitud (m)",
                "Área de sección (cm²)", "Origen del área o del peso", "Densidad (kg/m³)", "Peso (kg)", "Volumen Revit (m³)",
                "Partida", "Código (ARBA)", "Pernos (und)", "Origen (ARBA)");

            foreach (var e in r.AceroEstructural
                .OrderBy(c => c.Categoria).ThenBy(c => c.Partida).ThenBy(c => c.ElevacionNivel).ThenBy(c => c.Familia).ThenBy(c => c.Tipo))
            {
                Numero(hoja.Cell(fila, 1), IdNumerico(e.Id), "0");
                hoja.Cell(fila, 2).Value = e.Categoria;
                hoja.Cell(fila, 3).Value = e.Nivel;
                hoja.Cell(fila, 4).Value = e.Familia;
                hoja.Cell(fila, 5).Value = e.Tipo;
                hoja.Cell(fila, 6).SetValue(e.Marca ?? string.Empty);
                hoja.Cell(fila, 7).Value = e.Material;
                Numero(hoja.Cell(fila, 8), e.LongitudM, FormatoM);
                Numero(hoja.Cell(fila, 9), e.AreaSeccionCm2, FormatoM);
                hoja.Cell(fila, 10).SetValue(e.FuenteArea ?? string.Empty);
                Numero(hoja.Cell(fila, 11), e.DensidadKgM3, FormatoEntero);
                Numero(hoja.Cell(fila, 12), e.PesoKg, FormatoKg);
                if (e.VolumenM3 > 0) Numero(hoja.Cell(fila, 13), e.VolumenM3, FormatoM3);
                hoja.Cell(fila, 14).SetValue(e.Partida ?? string.Empty);
                hoja.Cell(fila, 15).SetValue(e.Codigo ?? string.Empty);
                if (e.Pernos > 0) Numero(hoja.Cell(fila, 16), e.Pernos, FormatoEntero);
                hoja.Cell(fila, 17).SetValue(e.Origen ?? string.Empty);
                fila++;
            }

            if (fila > 2)
            {
                var rango = hoja.Range(1, 1, fila - 1, nCol);
                rango.SetAutoFilter();
                Bordes(rango);
            }
            hoja.SheetView.FreezeRows(1);
            AjustarColumnas(hoja, nCol);
        }

        private static void EscribirDetalleAcero(IXLWorksheet hoja, ResultadoMetrado r)
        {
            int fila = 1;
            Encabezado(hoja, fila++, "Id", "Id anfitrión", "Elemento", "Nivel", "Partición", "Tipo de barra", "Diámetro (mm)",
                "N° barras", "Longitud por barra (m)", "Longitud total (m)", "Área malla (m²)", "Peso (kg)", "Origen de la longitud");

            foreach (var a in r.Acero
                .OrderBy(x => x.CategoriaHost).ThenBy(x => x.ElevacionNivel).ThenBy(x => x.DiametroMm))
            {
                Numero(hoja.Cell(fila, 1), IdNumerico(a.Id), "0");
                Numero(hoja.Cell(fila, 2), IdNumerico(a.HostId), "0");
                hoja.Cell(fila, 3).Value = a.CategoriaHost;
                hoja.Cell(fila, 4).Value = a.Nivel;
                hoja.Cell(fila, 5).SetValue(a.Particion ?? string.Empty);
                hoja.Cell(fila, 6).Value = a.TipoBarra;
                if (a.EsMalla) hoja.Cell(fila, 7).Value = "Malla";
                else Numero(hoja.Cell(fila, 7), a.DiametroMm, "0.##");
                Numero(hoja.Cell(fila, 8), a.Cantidad, FormatoEntero);
                Numero(hoja.Cell(fila, 9), a.LongitudUnaBarraM, FormatoM);
                Numero(hoja.Cell(fila, 10), a.LongitudTotalM, FormatoM);
                if (a.EsMalla) Numero(hoja.Cell(fila, 11), a.AreaM2, FormatoM);
                Numero(hoja.Cell(fila, 12), a.PesoKg, FormatoKg);
                hoja.Cell(fila, 13).SetValue(a.FuenteLongitud ?? string.Empty);
                fila++;
            }

            if (fila > 2)
            {
                var rango = hoja.Range(1, 1, fila - 1, 13);
                rango.SetAutoFilter();
                Bordes(rango);
            }
            hoja.SheetView.FreezeRows(1);
            AjustarColumnas(hoja, 13);
        }

        // ------------------------------------------------------------------
        // Estilos
        // ------------------------------------------------------------------

        /// <summary>
        /// True si la categoría se agrupa por nivel en las hojas. Vigas y cimentaciones no
        /// (igual que en las tablas de Revit): se agrupan solo por tipo.
        /// </summary>
        private bool AgruparPorNivel(string categoria) =>
            _opciones.Categorias.FirstOrDefault(c => c.Nombre == categoria)?.AgruparPorNivel ?? true;

        private static string NombreTipo(string familia, string tipo)
        {
            if (string.IsNullOrEmpty(familia) || familia == tipo) return tipo;
            return familia + ": " + tipo;
        }

        private static double IdNumerico(Autodesk.Revit.DB.ElementId id)
        {
            // ToString devuelve el valor numérico en todas las versiones de la API.
            return double.TryParse(id.ToString(), out double v) ? v : 0;
        }

        private static void Titulo(IXLWorksheet hoja, int fila, int columnas, string texto)
        {
            var rango = hoja.Range(fila, 1, fila, columnas);
            rango.Merge();
            hoja.Cell(fila, 1).Value = texto;
            rango.Style.Font.Bold = true;
            rango.Style.Font.FontSize = 14;
            rango.Style.Font.FontColor = XLColor.White;
            rango.Style.Fill.BackgroundColor = ColorTitulo;
            rango.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        private static void Subtitulo(IXLWorksheet hoja, int fila, int columnas, string texto)
        {
            var rango = hoja.Range(fila, 1, fila, columnas);
            rango.Merge();
            hoja.Cell(fila, 1).Value = texto;
            rango.Style.Font.Bold = true;
            rango.Style.Fill.BackgroundColor = ColorGrupo;
        }

        private static void Encabezado(IXLWorksheet hoja, int fila, params string[] textos)
        {
            for (int i = 0; i < textos.Length; i++)
            {
                hoja.Cell(fila, i + 1).Value = textos[i];
            }
            var rango = hoja.Range(fila, 1, fila, textos.Length);
            rango.Style.Font.Bold = true;
            rango.Style.Fill.BackgroundColor = ColorEncabezado;
            rango.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            rango.Style.Alignment.WrapText = true;
        }

        private static void Numero(IXLCell celda, double valor, string formato)
        {
            celda.Value = valor;
            celda.Style.NumberFormat.Format = formato;
        }

        private static void FilaResaltada(IXLWorksheet hoja, int fila, int columnas, XLColor color, bool negrita)
        {
            var rango = hoja.Range(fila, 1, fila, columnas);
            rango.Style.Fill.BackgroundColor = color;
            rango.Style.Font.Bold = negrita;
        }

        private static void Bordes(IXLRange rango)
        {
            rango.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rango.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        }

        private static void AjustarColumnas(IXLWorksheet hoja, int columnas)
        {
            hoja.Columns(1, columnas).AdjustToContents();
            foreach (var col in hoja.Columns(1, columnas))
            {
                if (col.Width < 12) col.Width = 12;
                if (col.Width > 50) col.Width = 50;
            }
        }
    }
}
