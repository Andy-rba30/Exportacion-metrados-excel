using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using ExportacionMetrados.Core.Metrado;
using Microsoft.Win32;

namespace ExportacionMetrados.UI
{
    /// <summary>
    /// Ventana de opciones del metrado automático.
    /// </summary>
    public partial class MetradoAutomaticoWindow : Window
    {
        private readonly OpcionesMetrado _opciones = new OpcionesMetrado();

        public MetradoAutomaticoWindow(string nombreSugerido)
        {
            InitializeComponent();
            LstCategorias.ItemsSource = _opciones.Categorias;

            string carpeta = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            TxtRuta.Text = Path.Combine(carpeta, nombreSugerido ?? "Metrado.xlsx");
        }

        /// <summary>Opciones elegidas. Válido solo si ShowDialog devolvió true.</summary>
        public OpcionesMetrado Opciones => _opciones;

        private void BtnExaminar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Guardar metrado como",
                Filter = "Libro de Excel (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true,
            };
            try
            {
                dlg.InitialDirectory = Path.GetDirectoryName(TxtRuta.Text);
                dlg.FileName = Path.GetFileName(TxtRuta.Text);
            }
            catch (ArgumentException) { }

            if (dlg.ShowDialog(this) == true) TxtRuta.Text = dlg.FileName;
        }

        private void BtnExportar_Click(object sender, RoutedEventArgs e)
        {
            if (!_opciones.Categorias.Any(c => c.Seleccionada))
            {
                MessageBox.Show(this, "Seleccione al menos un tipo de elemento.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ruta = TxtRuta.Text?.Trim();
            if (string.IsNullOrEmpty(ruta))
            {
                MessageBox.Show(this, "Indique el archivo de destino.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ruta.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) ruta += ".xlsx";
            if (ruta.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                MessageBox.Show(this, "La ruta contiene caracteres no válidos.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string textoDensidad = TxtDensidad.Text?.Trim().Replace(',', '.');
            if (!double.TryParse(textoDensidad, NumberStyles.Float, CultureInfo.InvariantCulture, out double densidad) || densidad <= 0)
            {
                MessageBox.Show(this, "La densidad del acero debe ser un número mayor que cero.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (File.Exists(ruta))
            {
                var r = MessageBox.Show(this, "El archivo ya existe. ¿Desea reemplazarlo?", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            _opciones.RutaArchivo = ruta;
            _opciones.SoloMaterialConcreto = ChkSoloConcreto.IsChecked == true;
            _opciones.IncluirAcero = ChkAcero.IsChecked == true;
            _opciones.IncluirDetalle = ChkDetalle.IsChecked == true;
            _opciones.AbrirAlTerminar = ChkAbrir.IsChecked == true;
            _opciones.DensidadAcero = densidad;

            DialogResult = true;
        }
    }
}
