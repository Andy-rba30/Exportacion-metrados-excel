using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using ExportacionMetrados.Core.Metrado;
using ExportacionMetrados.Core.Metrado.Encofrado;
using Microsoft.Win32;

namespace ExportacionMetrados.UI
{
    /// <summary>Ventana de opciones del metrado de encofrado.</summary>
    public partial class MetradoEncofradoWindow : Window
    {
        private readonly OpcionesEncofrado _opciones = new OpcionesEncofrado();

        public MetradoEncofradoWindow(string nombreSugerido)
        {
            InitializeComponent();
            LstReglas.ItemsSource = _opciones.Reglas;
            TxtContrato.Text = "Contrato ARBA-comun " + ClasificadorElementos.VersionContrato;

            string carpeta = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            TxtRuta.Text = Path.Combine(carpeta, nombreSugerido ?? "Metrado encofrado.xlsx");
        }

        /// <summary>Opciones elegidas. Válido solo si ShowDialog devolvió true.</summary>
        public OpcionesEncofrado Opciones => _opciones;

        private void BtnExaminar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Guardar metrado de encofrado como",
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

        private void ChkExcel_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlExcel != null) PnlExcel.IsEnabled = ChkExcel.IsChecked == true;
        }

        private void ChkTablas_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlTablas != null) PnlTablas.IsEnabled = ChkTablas.IsChecked == true;
        }

        private void BtnCalcular_Click(object sender, RoutedEventArgs e)
        {
            var reglas = _opciones.Reglas.Where(r => r.Seleccionada).ToList();
            if (reglas.Count == 0)
            {
                MessageBox.Show(this, "Marque al menos un tipo de elemento.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (reglas.All(r => !r.Laterales && !r.Fondo))
            {
                MessageBox.Show(this, "Ningún elemento marcado tiene caras que contar (laterales o fondo).", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string textoTolerancia = TxtTolerancia.Text?.Trim().Replace(',', '.');
            if (!double.TryParse(textoTolerancia, NumberStyles.Float, CultureInfo.InvariantCulture, out double tolerancia) || tolerancia <= 0 || tolerancia > 100)
            {
                MessageBox.Show(this, "La tolerancia de contacto debe ser un número de milímetros entre 0 y 100.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtTolerancia.Focus();
                return;
            }

            bool exportarExcel = ChkExcel.IsChecked == true;
            string ruta = TxtRuta.Text?.Trim();
            if (exportarExcel)
            {
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
                if (File.Exists(ruta))
                {
                    var r = MessageBox.Show(this, "El archivo ya existe. ¿Desea reemplazarlo?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                }
            }

            bool crearTablas = ChkTablas.IsChecked == true;
            if (crearTablas && ChkRegenerar.IsChecked == true)
            {
                var r = MessageBox.Show(this,
                    "Se borrarán las tablas de encofrado existentes con el mismo nombre y se crearán de nuevo. ¿Continuar?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }

            _opciones.FondoLosas = RbFondoSiempre.IsChecked == true ? ReglaFondoLosas.Siempre
                                 : RbFondoNunca.IsChecked == true ? ReglaFondoLosas.Nunca
                                 : ReglaFondoLosas.SalvoNivelMasBajo;
            _opciones.DescontarContactos = ChkDescontar.IsChecked == true;
            _opciones.ToleranciaContactoMm = tolerancia;
            _opciones.EscribirParametro = ChkParametro.IsChecked == true;
            _opciones.CrearTablas = crearTablas;
            _opciones.TablaGeneral = ChkTablaGeneral.IsChecked == true;
            _opciones.RegenerarTablasExistentes = ChkRegenerar.IsChecked == true;
            _opciones.AbrirTablaAlTerminar = ChkAbrirTabla.IsChecked == true;
            _opciones.ReservarSubproyectos = ChkSubproyectos.IsChecked == true;
            _opciones.ExportarExcel = exportarExcel;
            _opciones.RutaArchivo = ruta;
            _opciones.IncluirContactos = ChkContactos.IsChecked == true;
            _opciones.AbrirAlTerminar = ChkAbrir.IsChecked == true;

            DialogResult = true;
        }
    }
}
