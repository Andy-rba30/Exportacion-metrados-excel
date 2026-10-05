using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Arba.Comun;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado.Encofrado
{
    /// <summary>Tipo de cara de un elemento de concreto según la dirección de su normal.</summary>
    public enum CaraEncofrado
    {
        /// <summary>Cara vertical o casi vertical (costados, bordes, testeros).</summary>
        Lateral,
        /// <summary>Cara que mira hacia abajo (fondo de viga, sofito de losa).</summary>
        Fondo,
        /// <summary>Cara que mira hacia arriba: superficie libre o apoyo de otro elemento; nunca se encofra.</summary>
        Superior,
    }

    /// <summary>Qué hacer con el fondo (sofito) de las losas.</summary>
    public enum ReglaFondoLosas
    {
        /// <summary>Contar el fondo salvo en las losas apoyadas en el nivel más bajo del proyecto (losas sobre terreno).</summary>
        SalvoNivelMasBajo,
        /// <summary>Contar siempre el fondo.</summary>
        Siempre,
        /// <summary>No contar nunca el fondo.</summary>
        Nunca,
    }

    /// <summary>
    /// Qué caras se encofran en un grupo de elementos (vigas, columnas, cimentaciones, losas,
    /// muros). Las caras superiores no se encofran nunca; las superficies en contacto con otro
    /// elemento de concreto se descuentan siempre que la opción esté activa.
    /// </summary>
    public class ReglaEncofrado : INotifyPropertyChanged
    {
        private bool _seleccionada = true;
        private bool _laterales;
        private bool _fondo;

        public ReglaEncofrado(CategoriaMetrado grupo, bool laterales, bool fondo, string descripcion)
        {
            Grupo = grupo ?? throw new ArgumentNullException(nameof(grupo));
            _laterales = laterales;
            _fondo = fondo;
            Descripcion = descripcion;
        }

        public CategoriaMetrado Grupo { get; }
        public string Nombre => Grupo.Nombre;
        /// <summary>Qué caras se encofran en este grupo y por qué (para la ventana).</summary>
        public string Descripcion { get; }

        /// <summary>Metrar el encofrado de este grupo.</summary>
        public bool Seleccionada
        {
            get => _seleccionada;
            set { if (_seleccionada != value) { _seleccionada = value; Notificar(nameof(Seleccionada)); } }
        }

        /// <summary>Contar las caras laterales (verticales): costados de vigas y columnas, bordes de losas y zapatas, caras de muros.</summary>
        public bool Laterales
        {
            get => _laterales;
            set { if (_laterales != value) { _laterales = value; Notificar(nameof(Laterales)); } }
        }

        /// <summary>Contar las caras que miran hacia abajo: fondo de vigas, sofito de losas.</summary>
        public bool Fondo
        {
            get => _fondo;
            set { if (_fondo != value) { _fondo = value; Notificar(nameof(Fondo)); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notificar(string propiedad) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propiedad));

        /// <summary>
        /// Reglas predeterminadas para los grupos de una categoría del catálogo (vigas, columnas,
        /// cimentaciones, losas, muros), en el orden del catálogo.
        /// </summary>
        public static List<ReglaEncofrado> Predeterminadas(IList<CategoriaMetrado> catalogo)
        {
            var reglas = new List<ReglaEncofrado>();
            foreach (CategoriaMetrado g in catalogo.Where(c => c.Tipo == TipoGrupo.Categoria))
            {
                switch (g.NombreParticion)
                {
                    case ArbaContract.CatVigas:
                        reglas.Add(new ReglaEncofrado(g, true, true,
                            "Costados y fondo de la viga, más los testeros libres (volados). Se descuenta lo que entra en columnas o " +
                            "muros, el espesor de la losa que apoya en sus costados y la sección de las vigas secundarias que llegan a ella."));
                        break;
                    case ArbaContract.CatColumnas:
                        reglas.Add(new ReglaEncofrado(g, true, false,
                            "Solo las caras laterales. Se descuenta la sección de las vigas que llegan a la columna y la franja del " +
                            "espesor de la losa que la atraviesa; la cara superior y la base (sobre la zapata) no se encofran."));
                        break;
                    case ArbaContract.CatCimientos:
                        reglas.Add(new ReglaEncofrado(g, true, false,
                            "Solo los bordes (caras laterales): el fondo apoya en el terreno y la cara superior queda libre. Se descuenta " +
                            "el contacto con zapatas o cimientos vecinos. Las caras inclinadas de más de 45° cuentan como laterales."));
                        break;
                    case ArbaContract.CatLosas:
                        reglas.Add(new ReglaEncofrado(g, true, true,
                            "Fondo (sofito) más los bordes libres y los bordes de los vanos. Del fondo se descuenta el ancho de las vigas " +
                            "y muros en que apoya; de los bordes, el contacto con vigas, muros y columnas. El fondo de las losas apoyadas " +
                            "en el nivel más bajo (sobre terreno) no se cuenta, según la opción de abajo."));
                        break;
                    case ArbaContract.CatMuros:
                        reglas.Add(new ReglaEncofrado(g, true, false,
                            "Las dos caras y los extremos libres. Se descuentan las losas y vigas que entran en el muro, las columnas " +
                            "embebidas y los muros que se le unen; la coronación y la base no se encofran."));
                        break;
                }
            }
            return reglas;
        }
    }

    /// <summary>Opciones del metrado de encofrado.</summary>
    public class OpcionesEncofrado
    {
        public OpcionesEncofrado()
        {
            Catalogo = CategoriaMetrado.Predeterminadas();
            // Para repartir los elementos en grupos (y reconocer conexiones y misceláneos, que no son
            // de concreto) todos los grupos del catálogo cuentan; qué se metra lo deciden las reglas.
            foreach (CategoriaMetrado c in Catalogo) c.Seleccionada = true;
            Reglas = ReglaEncofrado.Predeterminadas(Catalogo);
        }

        /// <summary>Grupos del metrado (todos marcados): decide a qué grupo pertenece cada elemento.</summary>
        public List<CategoriaMetrado> Catalogo { get; }

        /// <summary>Reglas por grupo: qué grupos se metran y qué caras cuentan.</summary>
        public List<ReglaEncofrado> Reglas { get; }

        /// <summary>Descontar las superficies en contacto con otros elementos de concreto (el contexto).</summary>
        public bool DescontarContactos { get; set; } = true;

        /// <summary>
        /// Separación máxima (mm) entre dos elementos para considerarlos en contacto. Absorbe las
        /// pequeñas holguras del modelado; a partir de ella (p. ej. una junta de dilatación) las dos
        /// caras se consideran libres y se encofran.
        /// </summary>
        public double ToleranciaContactoMm { get; set; } = 10;

        public ReglaFondoLosas FondoLosas { get; set; } = ReglaFondoLosas.SalvoNivelMasBajo;

        /// <summary>Escribir el resultado en el parámetro "Metrado - Encofrado (m²)" de cada elemento.</summary>
        public bool EscribirParametro { get; set; } = true;

        /// <summary>Crear las tablas "Metrado encofrado - {elemento}".</summary>
        public bool CrearTablas { get; set; } = true;

        /// <summary>Crear además "Metrado encofrado - General" (todos los grupos, varias categorías).</summary>
        public bool TablaGeneral { get; set; } = true;

        public bool RegenerarTablasExistentes { get; set; } = false;
        public bool AbrirTablaAlTerminar { get; set; } = true;
        public bool ReservarSubproyectos { get; set; } = true;

        public bool ExportarExcel { get; set; } = true;
        public string RutaArchivo { get; set; }
        /// <summary>Incluir en el Excel la hoja de contactos (qué se descontó de cada elemento y con quién).</summary>
        public bool IncluirContactos { get; set; } = true;
        public bool AbrirAlTerminar { get; set; } = true;

        /// <summary>Opciones del metrado general equivalentes (para reservar subproyectos y crear tablas).</summary>
        public OpcionesMetrado ComoOpcionesMetrado()
        {
            var seleccionados = new HashSet<string>(Reglas.Where(r => r.Seleccionada).Select(r => r.Grupo.NombreParticion));
            var op = new OpcionesMetrado { RegenerarTablasExistentes = RegenerarTablasExistentes, IncluirAcero = false, ReservarSubproyectos = ReservarSubproyectos };
            foreach (CategoriaMetrado c in op.Categorias) c.Seleccionada = seleccionados.Contains(c.NombreParticion);
            return op;
        }
    }

    /// <summary>Superficie de un elemento en contacto con otro elemento de concreto (se descuenta del encofrado).</summary>
    public class ContactoEncofrado
    {
        public ElementId ConId { get; set; }
        public string ConCategoria { get; set; }
        public string ConTipo { get; set; }
        public CaraEncofrado Cara { get; set; }
        public double AreaM2 { get; set; }
    }

    /// <summary>Encofrado de un elemento de concreto ya calculado.</summary>
    public class ElementoEncofrado
    {
        public ElementId Id { get; set; }
        /// <summary>Grupo del metrado (Vigas, Columnas, Cimentaciones, Losas, Muros).</summary>
        public string Grupo { get; set; }
        /// <summary>Texto de "Metrado - Elemento" del elemento (VIGAS, o el propio del usuario), informativo.</summary>
        public string ElementoTexto { get; set; }
        public string Nivel { get; set; }
        public double ElevacionNivel { get; set; }
        public string Familia { get; set; }
        public string Tipo { get; set; }
        public string Marca { get; set; }
        public string Material { get; set; }

        /// <summary>Caras laterales que cuentan, sin descontar (m²).</summary>
        public double LateralBrutaM2 { get; set; }
        /// <summary>Fondos que cuentan, sin descontar (m²).</summary>
        public double FondoBrutaM2 { get; set; }
        /// <summary>Caras superiores (no se encofran), informativo (m²).</summary>
        public double SuperiorM2 { get; set; }
        /// <summary>Fondos que la regla del grupo no cuenta (columnas, zapatas, losa sobre terreno), informativo (m²).</summary>
        public double FondoNoContadoM2 { get; set; }
        /// <summary>Contacto con otros elementos descontado de las caras laterales (m²).</summary>
        public double DescuentoLateralM2 { get; set; }
        /// <summary>Contacto con otros elementos descontado de los fondos (m²).</summary>
        public double DescuentoFondoM2 { get; set; }

        public double LateralM2 => Math.Max(0, LateralBrutaM2 - DescuentoLateralM2);
        public double FondoM2 => Math.Max(0, FondoBrutaM2 - DescuentoFondoM2);
        public double TotalM2 => LateralM2 + FondoM2;
        public double DescuentoM2 => DescuentoLateralM2 + DescuentoFondoM2;

        /// <summary>True si el fondo no se contó por la regla de losas sobre terreno o por opción.</summary>
        public bool FondoExcluido { get; set; }
        /// <summary>True si alguna cara se midió por muestreo (caras curvas) o un booleano falló: resultado aproximado.</summary>
        public bool Aproximado { get; set; }
        public int CarasCurvas { get; set; }
        public string Nota { get; set; }

        public List<ContactoEncofrado> Contactos { get; } = new List<ContactoEncofrado>();
    }

    public class ResultadoEncofrado
    {
        public List<ElementoEncofrado> Elementos { get; } = new List<ElementoEncofrado>();
        public List<string> Advertencias { get; } = new List<string>();
        /// <summary>Elementos de concreto del modelo que forman el contexto (incluidos los no metrados).</summary>
        public int ElementosConcreto { get; set; }
        public TimeSpan Duracion { get; set; }
        public int OperacionesBooleanas { get; set; }
        public int CarasMuestreadas { get; set; }

        public double TotalM2 => Elementos.Sum(e => e.TotalM2);
        public double LateralM2 => Elementos.Sum(e => e.LateralM2);
        public double FondoM2 => Elementos.Sum(e => e.FondoM2);
        public double DescuentoM2 => Elementos.Sum(e => e.DescuentoM2);
        public int Aproximados => Elementos.Count(e => e.Aproximado);
    }
}
