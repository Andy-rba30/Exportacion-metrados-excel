using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using ClosedXML.Excel;

namespace ExportacionMetrados.Core
{
    /// <summary>
    /// Escribe una o varias tablas de planificación en un libro de Excel,
    /// una hoja por tabla.
    /// </summary>
    public class ExportadorExcel
    {
        private readonly OpcionesExportacion _opciones;

        // Caracteres que Excel no admite en nombres de hoja.
        private static readonly Regex CaracteresInvalidosHoja = new Regex(@"[\[\]\*\?/\\:]", RegexOptions.Compiled);

        // Número con separador de miles opcional y decimales con punto o coma,
        // opcionalmente seguido de un símbolo de unidad (m, m², m³, kg, %, etc.).
        private static readonly Regex PatronNumero = new Regex(
            @"^\s*(?<signo>[-+])?\s*(?<num>\d{1,3}([.,\s]\d{3})*([.,]\d+)?|\d+([.,]\d+)?)\s*(?<unidad>[a-zA-Zº°²³%µ/]*)\s*$",
            RegexOptions.Compiled);

        public ExportadorExcel(OpcionesExportacion opciones)
        {
            _opciones = opciones ?? throw new ArgumentNullException(nameof(opciones));
        }

        public ResultadoExportacion Exportar(IList<ViewSchedule> tablas, string rutaArchivo)
        {
            if (tablas == null || tablas.Count == 0)
                throw new ArgumentException("No se seleccionó ninguna tabla.", nameof(tablas));
            if (string.IsNullOrWhiteSpace(rutaArchivo))
                throw new ArgumentException("Debe indicar la ruta del archivo de salida.", nameof(rutaArchivo));

            if (File.Exists(rutaArchivo) && !_opciones.Sobrescribir)
                throw new IOException($"El archivo ya existe:\n{rutaArchivo}");

            string carpeta = Path.GetDirectoryName(rutaArchivo);
            if (!string.IsNullOrEmpty(carpeta)) Directory.CreateDirectory(carpeta);

            var resultado = new ResultadoExportacion();
            var nombresUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var libro = new XLWorkbook())
            {
                foreach (ViewSchedule tabla in tablas)
                {
                    try
                    {
                        TablaExtraida datos = LectorTablas.Extraer(tabla, _opciones.IncluirEncabezados);
                        string nombreHoja = NombreHojaUnico(datos.Nombre, nombresUsados);
                        IXLWorksheet hoja = libro.Worksheets.Add(nombreHoja);
                        int filas = EscribirHoja(hoja, datos);

                        resultado.TablasExportadas++;
                        resultado.FilasEscritas += filas;
                    }
                    catch (Exception ex)
                    {
                        resultado.Errores.Add($"\"{tabla.Name}\": {ex.Message}");
                    }
                }

                if (resultado.TablasExportadas == 0)
                {
                    throw new InvalidOperationException(
                        "No se pudo exportar ninguna tabla.\n" + string.Join("\n", resultado.Errores));
                }

                // Guardar en archivo temporal y luego mover, para no dejar un .xlsx
                // corrupto si falla la escritura (p. ej. archivo abierto en Excel).
                string temporal = rutaArchivo + ".tmp";
                libro.SaveAs(temporal);
                try
                {
                    if (File.Exists(rutaArchivo)) File.Delete(rutaArchivo);
                    File.Move(temporal, rutaArchivo);
                }
                catch (IOException)
                {
                    File.Delete(temporal);
                    throw new IOException(
                        "No se pudo escribir el archivo. Verifique que no esté abierto en Excel:\n" + rutaArchivo);
                }
            }

            return resultado;
        }

        /// <summary>
        /// Escribe título, encabezados y filas en la hoja. Devuelve el número de filas de datos.
        /// </summary>
        private int EscribirHoja(IXLWorksheet hoja, TablaExtraida datos)
        {
            int columnas = Math.Max(1, datos.NumeroColumnas);
            int filaActual = 1;

            if (_opciones.IncluirTitulo)
            {
                IXLCell celdaTitulo = hoja.Cell(filaActual, 1);
                celdaTitulo.Value = datos.Nombre;
                if (_opciones.AplicarFormato)
                {
                    var rango = hoja.Range(filaActual, 1, filaActual, columnas);
                    rango.Merge();
                    rango.Style.Font.Bold = true;
                    rango.Style.Font.FontSize = 13;
                    rango.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    rango.Style.Fill.BackgroundColor = XLColor.FromArgb(0x1F, 0x4E, 0x78);
                    rango.Style.Font.FontColor = XLColor.White;
                }
                filaActual += 2; // fila en blanco de separación
            }

            int inicioEncabezado = filaActual;
            foreach (var filaEnc in datos.Encabezados)
            {
                EscribirFila(hoja, filaActual, filaEnc, convertirNumeros: false);
                filaActual++;
            }
            int finEncabezado = filaActual - 1;

            if (_opciones.AplicarFormato && finEncabezado >= inicioEncabezado)
            {
                var rangoEnc = hoja.Range(inicioEncabezado, 1, finEncabezado, columnas);
                rangoEnc.Style.Font.Bold = true;
                rangoEnc.Style.Fill.BackgroundColor = XLColor.FromArgb(0xD9, 0xE1, 0xF2);
                rangoEnc.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                rangoEnc.Style.Alignment.WrapText = true;
                rangoEnc.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                rangoEnc.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            int inicioDatos = filaActual;
            foreach (var fila in datos.Filas)
            {
                EscribirFila(hoja, filaActual, fila, _opciones.ConvertirNumeros);
                filaActual++;
            }
            int finDatos = filaActual - 1;

            if (_opciones.AplicarFormato)
            {
                if (finDatos >= inicioDatos)
                {
                    var rangoDatos = hoja.Range(inicioDatos, 1, finDatos, columnas);
                    rangoDatos.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rangoDatos.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
                }

                hoja.Columns(1, columnas).AdjustToContents();
                foreach (var col in hoja.Columns(1, columnas))
                {
                    if (col.Width < 10) col.Width = 10;
                    if (col.Width > 60) col.Width = 60;
                }

                if (finEncabezado >= inicioEncabezado)
                {
                    hoja.SheetView.FreezeRows(finEncabezado);
                }
            }

            return datos.Filas.Count;
        }

        private void EscribirFila(IXLWorksheet hoja, int fila, List<string> celdas, bool convertirNumeros)
        {
            for (int i = 0; i < celdas.Count; i++)
            {
                IXLCell celda = hoja.Cell(fila, i + 1);
                string texto = celdas[i];

                if (convertirNumeros && TryConvertirNumero(texto, out double valor, out string unidad))
                {
                    celda.Value = valor;
                    if (_opciones.AplicarFormato)
                    {
                        // Conserva la unidad como formato de número personalizado para
                        // que la celda se vea igual que en Revit pero siga siendo numérica.
                        celda.Style.NumberFormat.Format = string.IsNullOrEmpty(unidad)
                            ? "#,##0.00"
                            : "#,##0.00 \"" + unidad + "\"";
                    }
                }
                else
                {
                    // SetValue evita que ClosedXML interprete fórmulas ("=...") o fechas.
                    celda.SetValue(texto);
                }
            }
        }

        /// <summary>
        /// Intenta interpretar un texto de celda como número. Acepta separadores de
        /// miles y decimales en formato español o inglés y un sufijo de unidad.
        /// </summary>
        internal static bool TryConvertirNumero(string texto, out double valor, out string unidad)
        {
            valor = 0;
            unidad = null;
            if (string.IsNullOrWhiteSpace(texto)) return false;

            Match m = PatronNumero.Match(texto);
            if (!m.Success) return false;

            string num = m.Groups["num"].Value.Replace(" ", string.Empty);
            unidad = m.Groups["unidad"].Value;

            // Determinar cuál es el separador decimal: el último separador que aparece,
            // siempre que no esté seguido de exactamente 3 dígitos como grupo de miles
            // repetido (p. ej. "1.234.567" -> miles; "1.234,5" -> decimal coma).
            int ultimoPunto = num.LastIndexOf('.');
            int ultimaComa = num.LastIndexOf(',');
            char separadorDecimal = '\0';

            if (ultimoPunto >= 0 && ultimaComa >= 0)
            {
                separadorDecimal = ultimoPunto > ultimaComa ? '.' : ',';
            }
            else if (ultimoPunto >= 0 || ultimaComa >= 0)
            {
                char unico = ultimoPunto >= 0 ? '.' : ',';
                int pos = Math.Max(ultimoPunto, ultimaComa);
                int digitosDespues = num.Length - pos - 1;
                int ocurrencias = num.Split(unico).Length - 1;
                // Un solo separador con 3 dígitos después es ambiguo ("1.250" puede ser
                // 1250 o 1,25). Se interpreta según la cultura actual de Windows.
                if (ocurrencias == 1 && digitosDespues == 3)
                {
                    string sepCultura = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                    separadorDecimal = sepCultura == unico.ToString() ? unico : '\0';
                }
                else if (ocurrencias == 1)
                {
                    separadorDecimal = unico;
                }
                // Varias ocurrencias del mismo separador: son separadores de miles.
            }

            string normalizado;
            if (separadorDecimal == '\0')
            {
                normalizado = num.Replace(".", string.Empty).Replace(",", string.Empty);
            }
            else
            {
                char separadorMiles = separadorDecimal == '.' ? ',' : '.';
                normalizado = num.Replace(separadorMiles.ToString(), string.Empty)
                                 .Replace(separadorDecimal, '.');
            }

            if (!double.TryParse(normalizado, NumberStyles.Float, CultureInfo.InvariantCulture, out valor))
            {
                return false;
            }

            if (m.Groups["signo"].Value == "-") valor = -valor;
            return true;
        }

        private static string NombreHojaUnico(string nombre, HashSet<string> usados)
        {
            string limpio = CaracteresInvalidosHoja.Replace(nombre ?? "Tabla", "_").Trim();
            if (limpio.Length == 0) limpio = "Tabla";
            if (limpio.Length > 31) limpio = limpio.Substring(0, 31);

            string candidato = limpio;
            int n = 2;
            while (!usados.Add(candidato))
            {
                string sufijo = $" ({n++})";
                int largoBase = Math.Min(limpio.Length, 31 - sufijo.Length);
                candidato = limpio.Substring(0, largoBase) + sufijo;
            }
            return candidato;
        }
    }
}
