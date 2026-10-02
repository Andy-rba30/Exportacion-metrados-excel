using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>
    /// Categorías estructurales que el metrado automático puede procesar.
    /// </summary>
    public class CategoriaMetrado
    {
        public CategoriaMetrado(BuiltInCategory categoria, string nombre, bool seleccionada)
        {
            Categoria = categoria;
            Nombre = nombre;
            Seleccionada = seleccionada;
        }

        public BuiltInCategory Categoria { get; }
        public string Nombre { get; }
        public bool Seleccionada { get; set; }

        public static List<CategoriaMetrado> Predeterminadas() => new List<CategoriaMetrado>
        {
            new CategoriaMetrado(BuiltInCategory.OST_StructuralFraming,    "Vigas",         true),
            new CategoriaMetrado(BuiltInCategory.OST_StructuralColumns,    "Columnas",      true),
            new CategoriaMetrado(BuiltInCategory.OST_StructuralFoundation, "Cimentaciones", false),
            new CategoriaMetrado(BuiltInCategory.OST_Floors,               "Losas",         false),
            new CategoriaMetrado(BuiltInCategory.OST_Walls,                "Muros",         false),
        };
    }

    public class OpcionesMetrado
    {
        public string RutaArchivo { get; set; }
        public List<CategoriaMetrado> Categorias { get; set; } = CategoriaMetrado.Predeterminadas();

        /// <summary>Solo contar elementos cuyo material sea de concreto.</summary>
        public bool SoloMaterialConcreto { get; set; } = true;

        /// <summary>Incluir el metrado de acero de refuerzo.</summary>
        public bool IncluirAcero { get; set; } = true;

        /// <summary>Añadir hojas con el detalle elemento por elemento.</summary>
        public bool IncluirDetalle { get; set; } = true;

        /// <summary>Densidad del acero usada para calcular el peso (kg/m³).</summary>
        public double DensidadAcero { get; set; } = 7850.0;

        public bool AbrirAlTerminar { get; set; } = true;
    }

    /// <summary>Un elemento de concreto ya medido.</summary>
    public class ElementoConcreto
    {
        public ElementId Id { get; set; }
        public string Categoria { get; set; }
        public string Nivel { get; set; }
        public double ElevacionNivel { get; set; }
        public string Familia { get; set; }
        public string Tipo { get; set; }
        public string Marca { get; set; }
        public string Material { get; set; }
        /// <summary>Longitud (vigas) o altura (columnas) en metros.</summary>
        public double LongitudM { get; set; }
        public double VolumenM3 { get; set; }
    }

    /// <summary>Un conjunto de barras de refuerzo ya medido.</summary>
    public class BarraAcero
    {
        public ElementId Id { get; set; }
        public ElementId HostId { get; set; }
        /// <summary>Categoría del anfitrión (Vigas, Columnas, ...).</summary>
        public string CategoriaHost { get; set; }
        public string Nivel { get; set; }
        public double ElevacionNivel { get; set; }
        public string TipoBarra { get; set; }
        public double DiametroMm { get; set; }
        public int Cantidad { get; set; }
        public double LongitudTotalM { get; set; }
        public double PesoKg { get; set; }
        public string Particion { get; set; }
    }

    public class ResultadoMetrado
    {
        public List<ElementoConcreto> Concreto { get; } = new List<ElementoConcreto>();
        public List<BarraAcero> Acero { get; } = new List<BarraAcero>();
        public List<string> Advertencias { get; } = new List<string>();
        public int ElementosOmitidosPorMaterial { get; set; }
    }
}
