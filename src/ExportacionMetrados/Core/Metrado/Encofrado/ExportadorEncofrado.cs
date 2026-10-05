using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arba.Comun;
using Autodesk.Revit.DB;
using ClosedXML.Excel;

namespace ExportacionMetrados.Core.Metrado.Encofrado
{
    /// <summary>
    /// Escribe el metrado de encofrado en un libro de Excel: Resumen (por elemento, por tipo y por
    /// nivel), una hoja por tabla de Revit creada, "Encofrado - Detalle" (una fila por elemento con
    /// caras brutas, descuentos y neto) y "Contactos" (qué se descontó de cada elemento y con qué
    /// elemento vecino), para poder verificar el cálculo.
    /// </summary>
    public class ExportadorEncofrado
    {
        private static readonly XLColor ColorTitulo = XLColor.FromArgb(0x1F, 0x4E, 0x78);
        private static readonly XLColor ColorEncabezado = XLColor.FromArgb(0xD9, 0xE1, 0xF2);
        private static readonly XLColor ColorGrupo = XLColor.FromArgb(0xF2, 0xF2, 0xF2);
        private static readonly XLColor ColorTotal = XLColor.FromArgb(0xFC, 0xE4, 0xD6);

        private const string FormatoM2 = "#,##0.00";
        private const string FormatoEntero = "#,##0";

        private readonly OpcionesEncofrado _op;

        public ExportadorEncofrado(OpcionesEncofrado opciones)
        {
            _op = opciones ?? throw new ArgumentNullException(nameof(opciones));
        }

        /// <summary>Escribe el libro en <see cref="OpcionesEncofrado.RutaArchivo"/>. Devuelve los errores no fatales.</summary>
        public List<string> Exportar(ResultadoEncofrado resultado, string tituloProyecto, IList<ViewSchedule> tablasRevit)
        {
            var errores = new List<string>();
            var grupos = _op.Reglas.Where(r => r.Seleccionada).Select(r => r.Grupo.Nombre).ToList();

            using (var libro = new XLWorkbook())
            {
                EscribirResumen(libro.Worksheets.Add("Resumen"), resultado, grupos, tituloProyecto);

                if (tablasRevit != null && tablasRevit.Count > 0)
                {
                    var exportadorTablas = new ExportadorExcel(new OpcionesExportacion());
                    var nombresUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Resumen", "Encofrado - Detalle", "Contactos" };
                    foreach (ViewSchedule tabla in tablasRevit)
                    {
                        try { exportadorTablas.AgregarHoja(libro, tabla, nombresUsados); }
                        catch (Exception ex) { errores.Add($"Tabla \"{tabla.Name}\": {ex.Message}"); }
                    }
                }

                EscribirDetalle(libro.Worksheets.Add("Encofrado - Detalle"), resultado, grupos);
                if (_op.IncluirContactos) EscribirContactos(libro.Worksheets.Add("Contactos"), resultado, grupos);

                ExportadorExcel.GuardarLibro(libro, _op.RutaArchivo);
            }
            return errores;
        }

        // ------------------------------------------------------------------
        // Resumen
        // ------------------------------------------------------------------

        private void EscribirResumen(IXLWorksheet hoja, ResultadoEncofrado r, List<string> grupos, string proyecto)
        {
            const int columnas = 7;
            int fila = 1;
            Titulo(hoja, fila++, columnas, "METRADO DE ENCOFRADO - " + proyecto);
            hoja.Cell(fila++, 1).Value = "Generado: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
            hoja.Cell(fila++, 1).Value = NotaCalculo(r);
            fila++;

            // Por elemento (grupo).
            Encabezado(hoja, fila++, "Elemento", "N° elementos", "Laterales (m²)", "Fondos (m²)", "Descuento por contacto (m²)", "Encofrado (m²)", "Caras que cuentan");
            int primera = fila;
            foreach (string grupo in grupos)
            {
                var del = r.Elementos.Where(e => e.Grupo == grupo).ToList();
                ReglaEncofrado regla = _op.Reglas.FirstOrDefault(x => x.Grupo.Nombre == grupo);
                hoja.Cell(fila, 1).Value = grupo;
                Numero(hoja.Cell(fila, 2), del.Count, FormatoEntero);
                Numero(hoja.Cell(fila, 3), del.Sum(e => e.LateralM2), FormatoM2);
                Numero(hoja.Cell(fila, 4), del.Sum(e => e.FondoM2), FormatoM2);
                Numero(hoja.Cell(fila, 5), del.Sum(e => e.DescuentoM2), FormatoM2);
                Numero(hoja.Cell(fila, 6), del.Sum(e => e.TotalM2), FormatoM2);
                hoja.Cell(fila, 7).Value = regla == null ? string.Empty : CarasDe(regla);
                fila++;
            }
            int ultima = fila - 1;
            hoja.Cell(fila, 1).Value = "TOTAL";
            foreach (int c in new[] { 2, 3, 4, 5, 6 })
            {
                string letra = ((char)('A' + c - 1)).ToString();
                hoja.Cell(fila, c).FormulaA1 = $"SUM({letra}{primera}:{letra}{ultima})";
                hoja.Cell(fila, c).Style.NumberFormat.Format = c == 2 ? FormatoEntero : FormatoM2;
            }
            FilaResaltada(hoja, fila, columnas, ColorTotal, true);
            Bordes(hoja.Range(primera - 1, 1, fila, columnas));
            fila += 2;

            // Por elemento y tipo.
            Subtitulo(hoja, fila++, columnas, "Por elemento y tipo");
            fila = Bloque(hoja, fila, r, grupos, "Tipo", e => NombreTipo(e.Familia, e.Tipo), (a, b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase));
            fila++;

            // Por elemento y nivel.
            Subtitulo(hoja, fila++, columnas, "Por elemento y nivel");
            var elevaciones = r.Elementos.GroupBy(e => e.Nivel).ToDictionary(g => g.Key, g => g.Min(e => e.ElevacionNivel));
            fila = Bloque(hoja, fila, r, grupos, "Nivel", e => e.Nivel,
                (a, b) => elevaciones[a].CompareTo(elevaciones[b]) != 0 ? elevaciones[a].CompareTo(elevaciones[b]) : string.CompareOrdinal(a, b));

            AjustarColumnas(hoja, columnas);
            hoja.Column(7).Width = 60;
        }

        /// <summary>Tabla Elemento | clave | N° | Laterales | Fondos | Descuento | Encofrado, con subtotal por grupo y total. Devuelve la fila siguiente.</summary>
        private static int Bloque(IXLWorksheet hoja, int fila, ResultadoEncofrado r, List<string> grupos, string clave,
            Func<ElementoEncofrado, string> claveDe, Comparison<string> orden)
        {
            Encabezado(hoja, fila++, "Elemento", clave, "N° elementos", "Laterales (m²)", "Fondos (m²)", "Descuento (m²)", "Encofrado (m²)");
            int primera = fila;
            var subtotales = new List<int>();
            foreach (string grupo in grupos)
            {
                var del = r.Elementos.Where(e => e.Grupo == grupo).ToList();
                if (del.Count == 0) continue;
                var claves = del.Select(claveDe).Distinct().ToList();
                claves.Sort(orden);
                int inicio = fila;
                foreach (string k in claves)
                {
                    var conClave = del.Where(e => claveDe(e) == k).ToList();
                    hoja.Cell(fila, 1).Value = grupo;
                    hoja.Cell(fila, 2).Value = k;
                    Numero(hoja.Cell(fila, 3), conClave.Count, FormatoEntero);
                    Numero(hoja.Cell(fila, 4), conClave.Sum(e => e.LateralM2), FormatoM2);
                    Numero(hoja.Cell(fila, 5), conClave.Sum(e => e.FondoM2), FormatoM2);
                    Numero(hoja.Cell(fila, 6), conClave.Sum(e => e.DescuentoM2), FormatoM2);
                    Numero(hoja.Cell(fila, 7), conClave.Sum(e => e.TotalM2), FormatoM2);
                    fila++;
                }
                hoja.Cell(fila, 1).Value = "Subtotal " + grupo;
                for (int c = 3; c <= 7; c++)
                {
                    string letra = ((char)('A' + c - 1)).ToString();
                    hoja.Cell(fila, c).FormulaA1 = $"SUM({letra}{inicio}:{letra}{fila - 1})";
                    hoja.Cell(fila, c).Style.NumberFormat.Format = c == 3 ? FormatoEntero : FormatoM2;
                }
                FilaResaltada(hoja, fila, 7, ColorGrupo, true);
                subtotales.Add(fila);
                fila++;
            }
            hoja.Cell(fila, 1).Value = "TOTAL";
            for (int c = 3; c <= 7; c++)
            {
                string letra = ((char)('A' + c - 1)).ToString();
                hoja.Cell(fila, c).FormulaA1 = subtotales.Count == 0 ? "0" : string.Join("+", subtotales.Select(s => letra + s));
                hoja.Cell(fila, c).Style.NumberFormat.Format = c == 3 ? FormatoEntero : FormatoM2;
            }
            FilaResaltada(hoja, fila, 7, ColorTotal, true);
            Bordes(hoja.Range(primera - 1, 1, fila, 7));
            return fila + 1;
        }

        private string NotaCalculo(ResultadoEncofrado r)
        {
            string fondoLosas;
            switch (_op.FondoLosas)
            {
                case ReglaFondoLosas.Siempre: fondoLosas = "el fondo de las losas se cuenta siempre"; break;
                case ReglaFondoLosas.Nunca: fondoLosas = "el fondo de las losas no se cuenta"; break;
                default: fondoLosas = "el fondo de las losas apoyadas en el nivel más bajo (sobre terreno) no se cuenta"; break;
            }
            return "Cálculo sobre la geometría del modelo: caras laterales y fondos según el elemento; las caras superiores no se encofran; " +
                   (_op.DescontarContactos
                       ? $"se descuentan las superficies en contacto con otros elementos de concreto (tolerancia {_op.ToleranciaContactoMm:0.#} mm); "
                       : "sin descontar contactos con otros elementos; ") +
                   fondoLosas + ". " +
                   $"Elementos de concreto en el modelo: {r.ElementosConcreto}; metrados: {r.Elementos.Count}" +
                   (r.Aproximados > 0 ? $"; con caras curvas o cálculo aproximado: {r.Aproximados}" : string.Empty) + ".";
        }

        private static string CarasDe(ReglaEncofrado regla)
        {
            var partes = new List<string>();
            if (regla.Laterales) partes.Add("laterales");
            if (regla.Fondo) partes.Add("fondo");
            return partes.Count == 0 ? "ninguna" : string.Join(" + ", partes) + " − contactos";
        }

        // ------------------------------------------------------------------
        // Detalle y contactos
        // ------------------------------------------------------------------

        private static void EscribirDetalle(IXLWorksheet hoja, ResultadoEncofrado r, List<string> grupos)
        {
            int fila = 1;
            Encabezado(hoja, fila++, "Id", "Elemento", "Metrado - Elemento", "Nivel", "Familia", "Tipo", "Marca", "Material",
                "Laterales brutas (m²)", "Fondos brutos (m²)", "Descuento laterales (m²)", "Descuento fondos (m²)",
                "Laterales (m²)", "Fondos (m²)", "Encofrado (m²)", "Caras superiores, no encofradas (m²)", "Fondo no contado (m²)", "Observaciones");
            const int columnas = 18;

            foreach (ElementoEncofrado e in r.Elementos
                .OrderBy(e => { int i = grupos.IndexOf(e.Grupo); return i < 0 ? int.MaxValue : i; })
                .ThenBy(e => e.ElevacionNivel).ThenBy(e => e.Familia).ThenBy(e => e.Tipo).ThenBy(e => ArbaRevit.IdValue(e.Id)))
            {
                Numero(hoja.Cell(fila, 1), ArbaRevit.IdValue(e.Id), "0");
                hoja.Cell(fila, 2).Value = e.Grupo;
                hoja.Cell(fila, 3).Value = e.ElementoTexto;
                hoja.Cell(fila, 4).Value = e.Nivel;
                hoja.Cell(fila, 5).Value = e.Familia;
                hoja.Cell(fila, 6).Value = e.Tipo;
                hoja.Cell(fila, 7).SetValue(e.Marca ?? string.Empty);
                hoja.Cell(fila, 8).Value = e.Material;
                Numero(hoja.Cell(fila, 9), e.LateralBrutaM2, FormatoM2);
                Numero(hoja.Cell(fila, 10), e.FondoBrutaM2, FormatoM2);
                Numero(hoja.Cell(fila, 11), e.DescuentoLateralM2, FormatoM2);
                Numero(hoja.Cell(fila, 12), e.DescuentoFondoM2, FormatoM2);
                Numero(hoja.Cell(fila, 13), e.LateralM2, FormatoM2);
                Numero(hoja.Cell(fila, 14), e.FondoM2, FormatoM2);
                Numero(hoja.Cell(fila, 15), e.TotalM2, FormatoM2);
                Numero(hoja.Cell(fila, 16), e.SuperiorM2, FormatoM2);
                Numero(hoja.Cell(fila, 17), e.FondoNoContadoM2, FormatoM2);
                hoja.Cell(fila, 18).Value = Observaciones(e);
                fila++;
            }

            if (fila > 2)
            {
                var rango = hoja.Range(1, 1, fila - 1, columnas);
                rango.SetAutoFilter();
                Bordes(rango);
            }
            hoja.SheetView.FreezeRows(1);
            AjustarColumnas(hoja, columnas);
        }

        private static string Observaciones(ElementoEncofrado e)
        {
            var partes = new List<string>();
            if (!string.IsNullOrEmpty(e.Nota)) partes.Add(e.Nota);
            if (e.CarasCurvas > 0) partes.Add($"{e.CarasCurvas} cara(s) curva(s) medidas por muestreo (aproximado)");
            else if (e.Aproximado) partes.Add("contacto aproximado (la geometría no admitió la operación exacta)");
            return string.Join(" ", partes);
        }

        private static void EscribirContactos(IXLWorksheet hoja, ResultadoEncofrado r, List<string> grupos)
        {
            int fila = 1;
            Encabezado(hoja, fila++, "Id", "Elemento", "Tipo", "Cara", "En contacto con (Id)", "Categoría del vecino", "Tipo del vecino", "Área descontada (m²)");
            const int columnas = 8;

            foreach (ElementoEncofrado e in r.Elementos
                .OrderBy(e => { int i = grupos.IndexOf(e.Grupo); return i < 0 ? int.MaxValue : i; })
                .ThenBy(e => ArbaRevit.IdValue(e.Id)))
            {
                foreach (ContactoEncofrado c in e.Contactos.OrderBy(c => c.Cara).ThenBy(c => ArbaRevit.IdValue(c.ConId)))
                {
                    Numero(hoja.Cell(fila, 1), ArbaRevit.IdValue(e.Id), "0");
                    hoja.Cell(fila, 2).Value = e.Grupo;
                    hoja.Cell(fila, 3).Value = NombreTipo(e.Familia, e.Tipo);
                    hoja.Cell(fila, 4).Value = c.Cara == CaraEncofrado.Fondo ? "Fondo" : "Lateral";
                    Numero(hoja.Cell(fila, 5), ArbaRevit.IdValue(c.ConId), "0");
                    hoja.Cell(fila, 6).Value = c.ConCategoria;
                    hoja.Cell(fila, 7).Value = c.ConTipo;
                    Numero(hoja.Cell(fila, 8), c.AreaM2, FormatoM2);
                    fila++;
                }
            }

            if (fila > 2)
            {
                var rango = hoja.Range(1, 1, fila - 1, columnas);
                rango.SetAutoFilter();
                Bordes(rango);
            }
            else
            {
                hoja.Cell(fila, 1).Value = "Ningún elemento está en contacto con otro (o la opción de descontar contactos está desactivada).";
            }
            hoja.Cell(fila + 1, 1).Value = "Cada fila es la superficie de una cara del elemento que coincide con el elemento vecino. " +
                                           "Si dos vecinos se solapan en la misma zona, la suma de sus filas puede superar el descuento del elemento, que no cuenta dos veces la misma superficie.";
            hoja.SheetView.FreezeRows(1);
            AjustarColumnas(hoja, columnas);
        }

        // ------------------------------------------------------------------
        // Formato
        // ------------------------------------------------------------------

        private static string NombreTipo(string familia, string tipo)
        {
            if (string.IsNullOrEmpty(familia) || familia == tipo) return tipo;
            return familia + ": " + tipo;
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
            for (int i = 0; i < textos.Length; i++) hoja.Cell(fila, i + 1).Value = textos[i];
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
