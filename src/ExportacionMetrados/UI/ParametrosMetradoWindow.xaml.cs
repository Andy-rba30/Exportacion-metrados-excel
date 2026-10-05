using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExportacionMetrados.Core.Metrado;

namespace ExportacionMetrados.UI
{
    /// <summary>Qué hace el comando "Parámetros y filtros" según la pestaña elegida.</summary>
    public enum ModoParametros
    {
        /// <summary>Escribir los parámetros del metrado y crear los filtros de vista, sin tablas.</summary>
        ParametrosYFiltros,
        /// <summary>Crear tablas a partir de los valores de "Metrado - Material" / "Metrado - Elemento" del modelo.</summary>
        TablasDesdeParametros,
    }

    /// <summary>
    /// Ventana del comando "Parámetros y filtros": pestaña 1, escribir los parámetros del metrado y
    /// los filtros de colores sin crear tablas; pestaña 2, crear tablas propias con los valores de
    /// "Metrado - Material" y "Metrado - Elemento" que haya en el modelo (incluidos los escritos a mano).
    /// </summary>
    public partial class ParametrosMetradoWindow : Window
    {
        private readonly OpcionesMetrado _opciones = new OpcionesMetrado();
        private readonly List<CombinacionMetrado> _combinaciones;

        /// <param name="combinaciones">Combinaciones leídas del modelo (<see cref="LectorCombinaciones.Leer"/>).</param>
        /// <param name="parametrosExisten">True si los parámetros del contrato ya existen en el proyecto.</param>
        public ParametrosMetradoWindow(List<CombinacionMetrado> combinaciones, bool parametrosExisten)
        {
            InitializeComponent();
            _combinaciones = combinaciones ?? new List<CombinacionMetrado>();

            LstCategorias.ItemsSource = _opciones.Categorias;
            LstCombinaciones.ItemsSource = _combinaciones;
            TxtContrato.Text = "Contrato ARBA-comun " + ClasificadorElementos.VersionContrato;

            int propias = _combinaciones.Count(c => !c.EsPredeterminada);
            if (!parametrosExisten)
            {
                TxtEstadoCombinaciones.Text = "El proyecto aún no tiene los parámetros del contrato: ejecute antes la pestaña 1.";
            }
            else if (_combinaciones.Count == 0)
            {
                TxtEstadoCombinaciones.Text = "Ningún elemento ni armadura tiene valor en \"Metrado - Material\" o \"Metrado - Elemento\": ejecute antes la pestaña 1.";
            }
            else
            {
                TxtEstadoCombinaciones.Text = $"{_combinaciones.Count} combinación(es) en el modelo, {propias} propia(s).";
            }
        }

        /// <summary>Pestaña elegida. Válido solo si ShowDialog devolvió true.</summary>
        public ModoParametros Modo { get; private set; }

        /// <summary>Opciones del paso 1 (y "Regenerar" / "Abrir tabla" del paso 2). Válido solo si ShowDialog devolvió true.</summary>
        public OpcionesMetrado Opciones => _opciones;

        /// <summary>Combinaciones marcadas en el paso 2.</summary>
        public List<CombinacionMetrado> CombinacionesSeleccionadas => _combinaciones.Where(c => c.Seleccionada).ToList();

        private void Pestanas_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BtnEjecutar == null || !ReferenceEquals(e.Source, Pestanas)) return;
            BtnEjecutar.Content = Pestanas.SelectedIndex == 1 ? "Crear tablas" : "Escribir parámetros y filtros";
        }

        private void BtnTodas_Click(object sender, RoutedEventArgs e)
        {
            foreach (CombinacionMetrado c in _combinaciones) c.Seleccionada = true;
        }

        private void BtnNinguna_Click(object sender, RoutedEventArgs e)
        {
            foreach (CombinacionMetrado c in _combinaciones) c.Seleccionada = false;
        }

        private void BtnPropias_Click(object sender, RoutedEventArgs e)
        {
            foreach (CombinacionMetrado c in _combinaciones) c.Seleccionada = !c.EsPredeterminada;
        }

        private void LstCombinaciones_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstCombinaciones.SelectedItem is CombinacionMetrado c) c.Seleccionada = !c.Seleccionada;
        }

        /// <summary>Lee una densidad (kg/m³) de la caja; admite coma o punto decimal.</summary>
        private bool LeerDensidad(TextBox caja, string mensaje, out double valor)
        {
            string texto = caja.Text?.Trim().Replace(',', '.');
            if (double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out valor) && valor > 0) return true;

            MessageBox.Show(this, mensaje, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Pestanas.SelectedIndex = 0;
            caja.Focus();
            return false;
        }

        private void BtnEjecutar_Click(object sender, RoutedEventArgs e)
        {
            if (Pestanas.SelectedIndex == 1)
            {
                if (CombinacionesSeleccionadas.Count == 0)
                {
                    MessageBox.Show(this, "Marque al menos una combinación para crear su tabla.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (ChkRegenerar.IsChecked == true)
                {
                    var r = MessageBox.Show(this,
                        "Se borrarán las tablas existentes con el mismo nombre y se crearán de nuevo. ¿Continuar?",
                        Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (r != MessageBoxResult.Yes) return;
                }

                Modo = ModoParametros.TablasDesdeParametros;
                _opciones.RegenerarTablasExistentes = ChkRegenerar.IsChecked == true;
                _opciones.AbrirTablaAlTerminar = ChkAbrirTabla.IsChecked == true;
                _opciones.NombreParametroPeso = TxtParametroPeso.Text?.Trim();
                DialogResult = true;
                return;
            }

            if (!_opciones.Categorias.Any(c => c.Seleccionada))
            {
                MessageBox.Show(this, "Seleccione al menos un tipo de elemento.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!LeerDensidad(TxtDensidad, "La densidad del acero de refuerzo debe ser un número mayor que cero.", out double densidad)) return;
            if (!LeerDensidad(TxtDensidadPerfiles, "La densidad del acero al carbono de los perfiles debe ser un número mayor que cero.", out double densidadPerfiles)) return;

            Modo = ModoParametros.ParametrosYFiltros;
            _opciones.ConservarClasificacionMaterial = ChkConservarMaterial.IsChecked == true;
            _opciones.ConservarElemento = ChkConservarElemento.IsChecked == true;
            _opciones.IncluirAcero = ChkRefuerzo.IsChecked == true;
            _opciones.RellenarParticiones = ChkParticiones.IsChecked == true;
            _opciones.SobrescribirParticiones = ChkSobrescribirParticiones.IsChecked == true;
            _opciones.CrearFiltrosVista = ChkFiltrosVista.IsChecked == true;
            _opciones.ReservarSubproyectos = ChkSubproyectos.IsChecked == true;
            _opciones.NombreParametroPeso = TxtParametroPeso.Text?.Trim();
            _opciones.DensidadAcero = densidad;
            _opciones.DensidadAceroEstructural = densidadPerfiles;
            // Sin tablas ni Excel: el peso de los perfiles se escribe siempre.
            _opciones.TablasAceroEstructural = true;
            _opciones.ExportarExcel = false;
            DialogResult = true;
        }
    }
}
