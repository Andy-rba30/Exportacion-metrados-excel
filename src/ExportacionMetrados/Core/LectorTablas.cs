using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core
{
    /// <summary>
    /// Lee las tablas de planificación de un documento y extrae sus celdas
    /// exactamente como se muestran en Revit (usando GetCellText, que respeta
    /// unidades, formatos, totales y agrupaciones).
    /// </summary>
    public static class LectorTablas
    {
        /// <summary>
        /// Devuelve las tablas de planificación que tiene sentido exportar
        /// (excluye plantillas, tablas de revisiones de cajetín y tablas internas).
        /// </summary>
        public static List<ViewSchedule> ObtenerTablasExportables(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate
                            && !v.IsTitleblockRevisionSchedule
                            && !v.IsInternalKeynoteSchedule
                            && !v.Name.StartsWith("<"))
                .OrderBy(v => v.Name)
                .ToList();
        }

        /// <summary>
        /// Extrae el contenido de una tabla como una matriz de textos.
        /// </summary>
        public static TablaExtraida Extraer(ViewSchedule tabla, bool incluirEncabezados)
        {
            var resultado = new TablaExtraida { Nombre = tabla.Name };

            TableData datos = tabla.GetTableData();

            if (incluirEncabezados)
            {
                TableSectionData encabezado = datos.GetSectionData(SectionType.Header);
                resultado.Encabezados.AddRange(LeerSeccion(tabla, encabezado, SectionType.Header));
            }

            TableSectionData cuerpo = datos.GetSectionData(SectionType.Body);
            resultado.Filas.AddRange(LeerSeccion(tabla, cuerpo, SectionType.Body));

            // Si la tabla no muestra encabezados en la sección Header (por ejemplo
            // cuando "Mostrar encabezados" está desactivado), se usan los nombres
            // de los campos visibles de la definición.
            if (incluirEncabezados && resultado.Encabezados.Count == 0)
            {
                var nombres = ObtenerNombresCampos(tabla);
                if (nombres.Count > 0)
                {
                    resultado.Encabezados.Add(nombres);
                }
            }

            return resultado;
        }

        private static IEnumerable<List<string>> LeerSeccion(ViewSchedule tabla, TableSectionData seccion, SectionType tipo)
        {
            if (seccion == null || seccion.NumberOfRows <= 0 || seccion.NumberOfColumns <= 0)
            {
                yield break;
            }

            int primeraFila = seccion.FirstRowNumber;
            int ultimaFila = seccion.LastRowNumber;
            int primeraCol = seccion.FirstColumnNumber;
            int ultimaCol = seccion.LastColumnNumber;

            for (int fila = primeraFila; fila <= ultimaFila; fila++)
            {
                var celdas = new List<string>(seccion.NumberOfColumns);
                bool filaVacia = true;

                for (int col = primeraCol; col <= ultimaCol; col++)
                {
                    string texto;
                    try
                    {
                        texto = tabla.GetCellText(tipo, fila, col) ?? string.Empty;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        texto = string.Empty;
                    }

                    if (texto.Length > 0) filaVacia = false;
                    celdas.Add(texto);
                }

                // Las filas en blanco de separación entre grupos no aportan datos.
                if (!filaVacia)
                {
                    yield return celdas;
                }
            }
        }

        private static List<string> ObtenerNombresCampos(ViewSchedule tabla)
        {
            var nombres = new List<string>();
            ScheduleDefinition definicion = tabla.Definition;
            int cantidad = definicion.GetFieldCount();
            for (int i = 0; i < cantidad; i++)
            {
                ScheduleField campo = definicion.GetField(i);
                if (!campo.IsHidden)
                {
                    nombres.Add(campo.ColumnHeading);
                }
            }
            return nombres;
        }
    }

    /// <summary>
    /// Contenido de una tabla ya leído de Revit, listo para escribirse.
    /// </summary>
    public class TablaExtraida
    {
        public string Nombre { get; set; }
        public List<List<string>> Encabezados { get; } = new List<List<string>>();
        public List<List<string>> Filas { get; } = new List<List<string>>();

        public int NumeroColumnas
        {
            get
            {
                int max = 0;
                foreach (var f in Encabezados) if (f.Count > max) max = f.Count;
                foreach (var f in Filas) if (f.Count > max) max = f.Count;
                return max;
            }
        }
    }
}
