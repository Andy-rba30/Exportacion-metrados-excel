using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Arba.Comun;
using Autodesk.Revit.DB;

namespace ExportacionMetrados.Core.Metrado
{
    /// <summary>Qué recoge una combinación: elementos (vigas, losas, escaleras...) o acero de refuerzo.</summary>
    public enum TipoCombinacion
    {
        Elementos,
        Refuerzo,
    }

    /// <summary>
    /// Una combinación de valores de "Metrado - Material" y "Metrado - Elemento" encontrada en el
    /// modelo (en los elementos: ambos; en el refuerzo: solo el elemento), con la que
    /// <see cref="GeneradorTablasRevit.GenerarDesdeParametros"/> crea una tabla filtrada por esos
    /// valores. Sirve para crear tablas propias, aparte de las predeterminadas, a partir de los
    /// textos que el usuario haya escrito en esos parámetros (p. ej. "ESCALERAS").
    /// </summary>
    public class CombinacionMetrado : INotifyPropertyChanged
    {
        private bool _seleccionada;

        public CombinacionMetrado(TipoCombinacion tipo, string material, string elemento)
        {
            Tipo = tipo;
            Material = material ?? string.Empty;
            Elemento = elemento ?? string.Empty;
        }

        public TipoCombinacion Tipo { get; }

        /// <summary>Texto exacto de "Metrado - Material" ("" en el refuerzo o si está vacío).</summary>
        public string Material { get; }

        /// <summary>Texto exacto de "Metrado - Elemento" ("" si está vacío).</summary>
        public string Elemento { get; }

        /// <summary>Categorías de Revit de los elementos con esta combinación y cuántos hay en cada una.</summary>
        public Dictionary<BuiltInCategory, int> Categorias { get; } = new Dictionary<BuiltInCategory, int>();

        /// <summary>Número de elementos (o conjuntos de barras) con esta combinación.</summary>
        public int NumeroElementos => Categorias.Values.Sum();

        /// <summary>
        /// True si la combinación ya la cubren las tablas predeterminadas del metrado automático
        /// (material CONCRETO / ACERO ESTRUCTURAL / MADERA / OTRO con un grupo del catálogo; refuerzo de
        /// VIGAS, COLUMNAS, CIMIENTOS, LOSAS o MUROS). Las propias son las demás.
        /// </summary>
        public bool EsPredeterminada { get; set; }

        /// <summary>Nombres de las categorías, para la ventana.</summary>
        public string NombresCategorias { get; set; } = string.Empty;

        public bool Seleccionada
        {
            get => _seleccionada;
            set
            {
                if (_seleccionada == value) return;
                _seleccionada = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Seleccionada)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>"Elementos" o "Refuerzo", para la ventana.</summary>
        public string TipoTexto => Tipo == TipoCombinacion.Refuerzo ? "Refuerzo" : "Elementos";

        /// <summary>Material para mostrar ("(vacío)" si no tiene; "—" en el refuerzo).</summary>
        public string MaterialTexto => Tipo == TipoCombinacion.Refuerzo ? "—" : (Material.Length > 0 ? Material : "(vacío)");

        /// <summary>Elemento para mostrar ("(vacío)" si no tiene).</summary>
        public string ElementoTexto => Elemento.Length > 0 ? Elemento : "(vacío)";

        /// <summary>"Predeterminada" o "Propia", para la ventana.</summary>
        public string OrigenTexto => EsPredeterminada ? "Predeterminada" : "Propia";

        /// <summary>
        /// Nombre base de la tabla: "Metrado acero - {elemento}" en el refuerzo; en los elementos,
        /// "Metrado {material en minúsculas} - {elemento}" ("Metrado concreto - ESCALERAS"), igual que
        /// las predeterminadas. Si la combinación abarca varias categorías de Revit se añade el
        /// nombre de cada categoría (ver <see cref="NombresTablas"/>).
        /// </summary>
        public string NombreTabla
        {
            get
            {
                if (Tipo == TipoCombinacion.Refuerzo) return GeneradorTablasRevit.PrefijoAcero + ElementoTexto;
                string material = Material.Length > 0 ? Material.ToLowerInvariant() : "elementos";
                return "Metrado " + material + " - " + ElementoTexto;
            }
        }

        /// <summary>Nombres de las tablas que se crearán (una por categoría de Revit si hay varias).</summary>
        public string NombresTablas
        {
            get
            {
                if (Tipo == TipoCombinacion.Refuerzo || Categorias.Count <= 1) return NombreTabla;
                return NombreTabla + " - <categoría> (" + Categorias.Count + " tablas)";
            }
        }
    }

    /// <summary>
    /// Lee del modelo los valores que tienen "Metrado - Material" y "Metrado - Elemento" en los
    /// elementos de las categorías del contrato y en el refuerzo, y los agrupa en combinaciones.
    /// Solo lee: no necesita transacción.
    /// </summary>
    public static class LectorCombinaciones
    {
        /// <summary>
        /// Combinaciones presentes en el modelo, primero las de elementos y luego las de refuerzo,
        /// ordenadas por material y elemento. Las que ya cubre el metrado automático vienen con
        /// <see cref="CombinacionMetrado.EsPredeterminada"/> = true. Vacío si los parámetros del
        /// contrato no existen aún en el proyecto.
        /// </summary>
        public static List<CombinacionMetrado> Leer(Document doc, IList<CategoriaMetrado> grupos)
        {
            var resultado = new List<CombinacionMetrado>();
            if (doc == null) return resultado;
            if (ClasificadorElementos.IdParametroMaterial(doc) == null && ClasificadorElementos.IdParametroElementoRefuerzo(doc) == null)
            {
                return resultado;
            }

            var materialesPredeterminados = new HashSet<string>(StringComparer.Ordinal)
            {
                ClasificadorElementos.ValorConcreto, ClasificadorElementos.ValorAceroEstructural,
                ClasificadorElementos.ValorMadera, ClasificadorElementos.ValorOtro,
            };
            var elementosPredeterminados = new HashSet<string>(grupos.Select(g => g.NombreParticion), StringComparer.Ordinal);
            var elementosConRefuerzo = new HashSet<string>(grupos.Where(g => g.AlojaRefuerzo).Select(g => g.NombreParticion), StringComparer.Ordinal);

            // 1. Elementos: categorías a las que el contrato vincula "Metrado - Material" o "Metrado - Elemento"
            //    (sin el refuerzo, que va aparte).
            var categorias = new List<BuiltInCategory>();
            foreach (string n in ArbaContract.Material.Categories.Concat(ArbaContract.Elemento.Categories))
            {
                BuiltInCategory bic = ArbaRevit.ParseCategory(n);
                if (bic == BuiltInCategory.INVALID || categorias.Contains(bic)) continue;
                if (ClasificadorElementos.CategoriasRefuerzo.Contains(bic)) continue;
                categorias.Add(bic);
            }

            var elementos = new Dictionary<string, CombinacionMetrado>(StringComparer.Ordinal);
            foreach (Element e in ArbaRevit.Instances(doc, categorias).ToElements())
            {
                try
                {
                    string material = ArbaSharedParams.GetText(e, ArbaContract.Material).Trim();
                    string elemento = ArbaSharedParams.GetText(e, ArbaContract.Elemento).Trim();
                    if (material.Length == 0 && elemento.Length == 0) continue;
                    BuiltInCategory bic = ArbaRevit.BuiltInOf(e.Category);
                    if (bic == BuiltInCategory.INVALID) continue;

                    string clave = material + "\n" + elemento;
                    if (!elementos.TryGetValue(clave, out CombinacionMetrado c))
                    {
                        c = new CombinacionMetrado(TipoCombinacion.Elementos, material, elemento)
                        {
                            EsPredeterminada = materialesPredeterminados.Contains(material) && elementosPredeterminados.Contains(elemento),
                        };
                        elementos[clave] = c;
                    }
                    c.Categorias.TryGetValue(bic, out int n);
                    c.Categorias[bic] = n + 1;
                }
                catch (Exception) { /* un elemento sin categoría o sin acceso a sus parámetros no cuenta */ }
            }

            // 2. Refuerzo: solo "Metrado - Elemento" (el material no aplica).
            var refuerzo = new Dictionary<string, CombinacionMetrado>(StringComparer.Ordinal);
            foreach (Element r in ClasificadorElementos.TodoElRefuerzo(doc))
            {
                try
                {
                    string elemento = ArbaSharedParams.GetText(r, ArbaContract.Elemento).Trim();
                    if (elemento.Length == 0) continue;
                    BuiltInCategory bic = ArbaRevit.BuiltInOf(r.Category);
                    if (bic == BuiltInCategory.INVALID) continue;

                    if (!refuerzo.TryGetValue(elemento, out CombinacionMetrado c))
                    {
                        c = new CombinacionMetrado(TipoCombinacion.Refuerzo, null, elemento)
                        {
                            EsPredeterminada = elementosConRefuerzo.Contains(elemento),
                        };
                        refuerzo[elemento] = c;
                    }
                    c.Categorias.TryGetValue(bic, out int n);
                    c.Categorias[bic] = n + 1;
                }
                catch (Exception) { }
            }

            resultado.AddRange(elementos.Values.OrderBy(c => c.Material, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Elemento, StringComparer.CurrentCultureIgnoreCase));
            resultado.AddRange(refuerzo.Values.OrderBy(c => c.Elemento, StringComparer.CurrentCultureIgnoreCase));

            foreach (CombinacionMetrado c in resultado)
            {
                c.NombresCategorias = string.Join(", ", c.Categorias.Keys.Select(bic => NombreCategoria(doc, bic)));
                c.Seleccionada = !c.EsPredeterminada;
            }
            return resultado;
        }

        /// <summary>Nombre visible de la categoría en el proyecto (o el del enumerado si no se resuelve).</summary>
        public static string NombreCategoria(Document doc, BuiltInCategory bic)
        {
            try { return Category.GetCategory(doc, bic)?.Name ?? bic.ToString(); }
            catch (Exception) { return bic.ToString(); }
        }
    }
}
