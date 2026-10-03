using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>Clase de grupo de metrado.</summary>
    public enum TipoGrupo
    {
        /// <summary>Una categoría de Revit: vigas, columnas, cimentaciones, losas, muros.</summary>
        Categoria,
        /// <summary>
        /// Conexiones y anclajes: pernos, anclajes, espárragos, planchas, cartelas y
        /// rigidizadores. Se reconocen por el nombre en cualquier categoría metálica
        /// (en un IFC suelen venir en Vigas o Columnas) además de por su categoría.
        /// </summary>
        Conexiones,
        /// <summary>Otros: varias categorías de Revit que no son viga, columna, cimentación, losa ni muro.</summary>
        Otros,
    }

    /// <summary>
    /// Grupo de elementos que el metrado automático procesa: una categoría de Revit
    /// (vigas, columnas, cimentaciones, losas, muros), "Conexiones y anclajes" (por
    /// nombre y categoría) u "Otros" (conexiones estructurales, modelos genéricos y
    /// cubiertas).
    /// </summary>
    public class CategoriaMetrado
    {
        public CategoriaMetrado(BuiltInCategory categoria, string nombre, string nombreParticion, bool seleccionada,
            string descripcion = null)
            : this(new[] { categoria }, nombre, nombreParticion, seleccionada, descripcion, TipoGrupo.Categoria)
        {
        }

        public CategoriaMetrado(IList<BuiltInCategory> categorias, string nombre, string nombreParticion, bool seleccionada,
            string descripcion, TipoGrupo tipo)
        {
            Categorias = new List<BuiltInCategory>(categorias);
            Nombre = nombre;
            NombreParticion = nombreParticion;
            Seleccionada = seleccionada;
            Descripcion = descripcion;
            Tipo = tipo;
        }

        public TipoGrupo Tipo { get; }

        /// <summary>Categorías de Revit que forman el grupo (una sola salvo en "Otros").</summary>
        public IReadOnlyList<BuiltInCategory> Categorias { get; }
        /// <summary>Primera categoría del grupo: la única en los grupos de una categoría.</summary>
        public BuiltInCategory Categoria => Categorias[0];
        /// <summary>True si el grupo reúne varias categorías de Revit.</summary>
        public bool EsGrupo => Categorias.Count > 1;
        public string Nombre { get; }
        /// <summary>
        /// Texto que identifica al grupo: se escribe en "Metrado - Elemento" de sus elementos
        /// y en el refuerzo que alojan (y en la partición vacía de ese refuerzo).
        /// </summary>
        public string NombreParticion { get; }
        public bool Seleccionada { get; set; }
        /// <summary>Explicación para la ventana (qué entra en el grupo y cómo se metra).</summary>
        public string Descripcion { get; }

        /// <summary>True en "Otros": lo que no es viga, columna, cimentación, losa ni muro.</summary>
        public bool EsOtros => Tipo == TipoGrupo.Otros;

        /// <summary>True en "Conexiones y anclajes": pernos, anclajes, planchas, rigidizadores.</summary>
        public bool EsConexiones => Tipo == TipoGrupo.Conexiones;

        /// <summary>True si el grupo admite elementos metálicos (perfiles, conexiones, planchas).</summary>
        public bool PuedeSerMetalica =>
            Tipo != TipoGrupo.Categoria ||
            Categoria == BuiltInCategory.OST_StructuralFraming || Categoria == BuiltInCategory.OST_StructuralColumns;

        /// <summary>
        /// True si el peso se calcula como volumen × densidad (piezas sin longitud ni
        /// sección: pernos, planchas, conexiones, coberturas) en lugar de longitud × sección × densidad.
        /// </summary>
        public bool PesoPorVolumen => Tipo != TipoGrupo.Categoria;

        /// <summary>True si el grupo puede alojar refuerzo (tablas y filtros de acero por elemento).</summary>
        public bool AlojaRefuerzo => Tipo == TipoGrupo.Categoria;

        /// <summary>
        /// True si la tabla de acero estructural del grupo es de varias categorías de Revit
        /// ("Otros" y "Conexiones y anclajes"), filtrada por "Metrado - Elemento".
        /// </summary>
        public bool TablaMulticategoria => Tipo != TipoGrupo.Categoria;

        /// <summary>
        /// True solo en columnas y muros: su metrado se agrupa por nivel. Vigas (pueden
        /// cruzar varios), losas (se metran por tipo en todo el edificio), cimentaciones
        /// (comparten el nivel de fundación), conexiones y "Otros" van solo por tipo. Vale
        /// tanto para las tablas de Revit como para el Excel.
        /// </summary>
        public bool AgruparPorNivel =>
            Tipo == TipoGrupo.Categoria &&
            (Categoria == BuiltInCategory.OST_StructuralColumns || Categoria == BuiltInCategory.OST_Walls);

        /// <summary>
        /// Ejemplares (no tipos) de las categorías propias del grupo. No aplica la
        /// asignación por nombre: para eso, <see cref="ClasificadorElementos.ElementosDelGrupo"/>.
        /// </summary>
        public FilteredElementCollector Elementos(Document doc) => Colector(doc, Categorias);

        /// <summary>Ejemplares (no tipos) de las categorías indicadas.</summary>
        public static FilteredElementCollector Colector(Document doc, IEnumerable<BuiltInCategory> categorias)
        {
            var lista = new List<BuiltInCategory>(categorias);
            var colector = new FilteredElementCollector(doc);
            if (lista.Count == 1) colector.OfCategory(lista[0]);
            else colector.WherePasses(new ElementMulticategoryFilter(lista));
            return colector.WhereElementIsNotElementType();
        }

        /// <summary>True si la categoría (id) pertenece al grupo.</summary>
        public bool Contiene(ElementId idCategoria)
        {
            if (idCategoria == null) return false;
            return Categorias.Any(c => idCategoria == new ElementId(c));
        }

        public static List<CategoriaMetrado> Predeterminadas() => new List<CategoriaMetrado>
        {
            new CategoriaMetrado(BuiltInCategory.OST_StructuralFraming,    "Vigas",         "VIGAS",     true,
                "Armazón estructural: vigas y arriostres de concreto (volumen) o metálicos (peso)."),
            new CategoriaMetrado(BuiltInCategory.OST_StructuralColumns,    "Columnas",      "COLUMNAS",  true,
                "Pilares estructurales de concreto (volumen) o metálicos (peso)."),
            new CategoriaMetrado(BuiltInCategory.OST_StructuralFoundation, "Cimentaciones", "CIMIENTOS", true,
                "Zapatas, vigas de cimentación, plateas y muros de contención de la categoría Cimentación estructural."),
            new CategoriaMetrado(BuiltInCategory.OST_Floors,               "Losas",         "LOSAS",     true,
                "Suelos estructurales: losas, solados."),
            new CategoriaMetrado(BuiltInCategory.OST_Walls,                "Muros",         "MUROS",     false,
                "Muros (placas) de concreto."),
            new CategoriaMetrado(
                new[] { BuiltInCategory.OST_StructuralStiffener },
                "Conexiones y anclajes", "CONEXIONES", true,
                "Pernos, anclajes, espárragos, tuercas, arandelas, planchas, cartelas y rigidizadores. Se reconocen por " +
                "el nombre en cualquier categoría metálica (en un IFC suelen venir como Vigas o Columnas) y se " +
                "pesan por volumen × densidad en \"Metrado acero estructural - Conexiones y anclajes\".",
                TipoGrupo.Conexiones),
            new CategoriaMetrado(
                new[] { BuiltInCategory.OST_StructConnections, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Roofs },
                "Otros", "OTROS", true,
                "Lo que no es viga, columna, cimentación, losa ni muro: conexiones estructurales (donde un IFC deja, por " +
                "ejemplo, las coberturas metálicas), modelos genéricos y cubiertas. Las piezas metálicas van a " +
                "\"Metrado acero estructural - Otros\" pesadas por volumen × densidad; las de concreto, a una tabla de " +
                "concreto por categoría.",
                TipoGrupo.Otros),
        };

        private static readonly List<CategoriaMetrado> Catalogo = Predeterminadas();

        /// <summary>Grupo del catálogo al que pertenece la categoría del elemento, o null si no se metra.</summary>
        public static CategoriaMetrado De(Element e) => DeCategoria(Catalogo, e?.Category?.Id);

        /// <summary>Grupo de la lista que contiene la categoría (id), o null.</summary>
        public static CategoriaMetrado DeCategoria(IEnumerable<CategoriaMetrado> grupos, ElementId idCategoria)
        {
            if (idCategoria == null) return null;
            return grupos.FirstOrDefault(g => g.Contiene(idCategoria));
        }
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

        /// <summary>Densidad del acero usada para calcular el peso (kg/m³) cuando no hay parámetro de peso.</summary>
        public double DensidadAcero { get; set; } = 7850.0;

        /// <summary>Nombre del parámetro del tipo de barra con el peso por metro (kg/m).</summary>
        public string NombreParametroPeso { get; set; } = "Bar Mass per Unit Length";

        /// <summary>
        /// Densidad del acero al carbono (kg/m³), el material de los perfiles estructurales.
        /// Se usa para pesar los perfiles metálicos: longitud × área de sección × densidad.
        /// </summary>
        public double DensidadAceroEstructural { get; set; } = 7850.0;

        /// <summary>Si ya existen tablas con el mismo nombre, borrarlas y crearlas de nuevo.</summary>
        public bool RegenerarTablasExistentes { get; set; } = false;

        /// <summary>
        /// Crear filtros de vista (Visibilidad/Gráficos) con un color por tipo de elemento
        /// metrado (concreto, acero estructural y refuerzo por partición) y aplicarlos a la
        /// vista activa para comprobar visualmente el metrado.
        /// </summary>
        public bool CrearFiltrosVista { get; set; } = true;

        /// <summary>
        /// En modelos compartidos, reservar de antemano los subproyectos que se van a
        /// modificar (evita el aviso de Revit "checkout a large number of elements").
        /// </summary>
        public bool ReservarSubproyectos { get; set; } = true;

        /// <summary>Añadir a las tablas de Revit un filtro "Material estructural contiene ...".</summary>
        public bool FiltrarPorMaterial { get; set; } = true;

        /// <summary>Texto del filtro de material en las tablas de Revit.</summary>
        public string TextoMaterialConcreto { get; set; } = "Concreto";

        /// <summary>Rellenar la partición vacía del refuerzo con el nombre de la categoría del anfitrión.</summary>
        public bool RellenarParticiones { get; set; } = true;

        /// <summary>Sobrescribir también las particiones que ya tengan texto.</summary>
        public bool SobrescribirParticiones { get; set; } = false;

        /// <summary>Conservar la clasificación de material ya escrita en los elementos.</summary>
        public bool ConservarClasificacionMaterial { get; set; } = false;

        /// <summary>Crear tablas aparte para los elementos de acero estructural (perfiles metálicos).</summary>
        public bool TablasAceroEstructural { get; set; } = true;

        /// <summary>Crear una tabla de acero de refuerzo por cada categoría de anfitrión.</summary>
        public bool TablasAceroPorElemento { get; set; } = true;

        /// <summary>Crear una tabla general de acero de refuerzo (todas las categorías).</summary>
        public bool TablaAceroGeneral { get; set; } = true;

        /// <summary>Exportar las tablas generadas a Excel en la misma operación.</summary>
        public bool ExportarExcel { get; set; } = true;

        /// <summary>Abrir la primera tabla generada en Revit al terminar.</summary>
        public bool AbrirTablaAlTerminar { get; set; } = true;

        /// <summary>Ofrecer abrir el archivo Excel al terminar.</summary>
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
        /// <summary>Área (losas, muros, cimentaciones) en m².</summary>
        public double AreaM2 { get; set; }
        /// <summary>Espesor (losas, muros) en metros.</summary>
        public double EspesorM { get; set; }
        public double VolumenM3 { get; set; }
    }

    /// <summary>
    /// Un perfil metálico (viga o columna de acero estructural) ya medido. Los
    /// perfiles no se metran por volumen sino por peso:
    /// peso = longitud × área de la sección × densidad.
    /// </summary>
    public class ElementoAceroEstructural
    {
        public ElementId Id { get; set; }
        public string Categoria { get; set; }
        public string Nivel { get; set; }
        public double ElevacionNivel { get; set; }
        public string Familia { get; set; }
        public string Tipo { get; set; }
        public string Marca { get; set; }
        public string Material { get; set; }
        /// <summary>Longitud del perfil en metros.</summary>
        public double LongitudM { get; set; }
        /// <summary>Área de la sección transversal en cm².</summary>
        public double AreaSeccionCm2 { get; set; }
        /// <summary>De dónde se obtuvo el área de la sección (parámetro del tipo, sección de la familia, volumen/longitud).</summary>
        public string FuenteArea { get; set; }
        /// <summary>Densidad del acero al carbono usada, en kg/m³.</summary>
        public double DensidadKgM3 { get; set; }
        /// <summary>Peso en kg = longitud × área de sección × densidad.</summary>
        public double PesoKg { get; set; }
        /// <summary>Volumen que informa Revit (m³), solo como referencia.</summary>
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
        /// <summary>Longitud de una sola pieza del conjunto (parámetro "Longitud de barra").</summary>
        public double LongitudUnaBarraM { get; set; }
        /// <summary>Longitud de todas las piezas del conjunto (parámetro "Longitud total de barra").</summary>
        public double LongitudTotalM { get; set; }
        /// <summary>De dónde se obtuvo la longitud total (parámetro o respaldo).</summary>
        public string FuenteLongitud { get; set; }
        public double PesoKg { get; set; }
        public string Particion { get; set; }
        /// <summary>True si es una malla electrosoldada (FabricSheet) en lugar de barras.</summary>
        public bool EsMalla { get; set; }
        /// <summary>Área de la malla en m² (solo mallas).</summary>
        public double AreaM2 { get; set; }
    }

    public class ResultadoMetrado
    {
        public List<ElementoConcreto> Concreto { get; } = new List<ElementoConcreto>();
        /// <summary>Perfiles metálicos pesados por longitud × área de sección × densidad.</summary>
        public List<ElementoAceroEstructural> AceroEstructural { get; } = new List<ElementoAceroEstructural>();
        public List<BarraAcero> Acero { get; } = new List<BarraAcero>();
        public List<string> Advertencias { get; } = new List<string>();
        public int ElementosOmitidosPorMaterial { get; set; }
    }
}
