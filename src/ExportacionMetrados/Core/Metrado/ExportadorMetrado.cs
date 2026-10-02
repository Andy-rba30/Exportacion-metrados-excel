using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Escribe el resultado del metrado automático en un libro de Excel con las
    /// hojas: Resumen, Concreto, Acero y (opcional) detalle por elemento.
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

        public void Exportar(ResultadoMetrado resultado, string tituloProyecto)
        {
            string ruta = _opciones.RutaArchivo;
            string carpeta = Path.GetDirectoryName(ruta);
            if (!string.IsNullOrEmpty(carpeta)) Directory.CreateDirectory(carpeta);

            var ordenCategorias = _opciones.Categorias.Where(c => c.Seleccionada).Select(c => c.Nombre).ToList();

            using (var libro = new XLWorkbook())
            {
                EscribirResumen(libro.Worksheets.Add("Resumen"), resultado, ordenCategorias, tituloProyecto);
                EscribirConcreto(libro.Worksheets.Add("Concreto"), resultado, ordenCategorias);

                if (_opciones.IncluirAcero)
                {
                    EscribirAcero(libro.Worksheets.Add("Acero"), resultado, ordenCategorias);
                }

                if (_opciones.IncluirDetalle)
                {
                    EscribirDetalleConcreto(libro.Worksheets.Add("Concreto - Detalle"), resultado);
                    if (_opciones.IncluirAcero)
                    {
                        EscribirDetalleAcero(libro.Worksheets.Add("Acero - Detalle"), resultado);
                    }
                }

                string temporal = ruta + ".tmp";
                libro.SaveAs(temporal);
                try
                {
                    if (File.Exists(ruta)) File.Delete(ruta);
                    File.Move(temporal, ruta);
                }
                catch (IOException)
                {
                    File.Delete(temporal);
                    throw new IOException(
                        "No se pudo escribir el archivo. Verifique que no esté abierto en Excel:\n" + ruta);
                }
            }
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
                var porNivel = elementos
                    .GroupBy(e => new { e.Nivel, e.ElevacionNivel })
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
