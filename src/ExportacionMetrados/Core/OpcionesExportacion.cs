namespace ExportacionMetrados.Core
{
    /// <summary>
    /// Opciones elegidas por el usuario en la ventana de exportación.
    /// </summary>
    public class OpcionesExportacion
    {
        /// <summary>Ruta completa del archivo .xlsx a generar.</summary>
        public string RutaArchivo { get; set; }

        /// <summary>Escribir el título de la tabla como primera fila de la hoja.</summary>
        public bool IncluirTitulo { get; set; } = true;

        /// <summary>Incluir las filas de encabezado (nombres de columna).</summary>
        public bool IncluirEncabezados { get; set; } = true;

        /// <summary>Convertir celdas con texto numérico a números de Excel.</summary>
        public bool ConvertirNumeros { get; set; } = true;

        /// <summary>Aplicar formato (negrita, bordes, ancho automático de columnas).</summary>
        public bool AplicarFormato { get; set; } = true;

        /// <summary>Si el archivo ya existe, sobrescribirlo.</summary>
        public bool Sobrescribir { get; set; } = true;

        /// <summary>Ofrecer abrir el archivo al terminar.</summary>
        public bool AbrirAlTerminar { get; set; } = true;
    }

    /// <summary>
    /// Resultado de una exportación: conteos y lista de advertencias.
    /// </summary>
    public class ResultadoExportacion
    {
        public int TablasExportadas { get; set; }
        public int FilasEscritas { get; set; }
        public System.Collections.Generic.List<string> Errores { get; } = new System.Collections.Generic.List<string>();
    }
}
