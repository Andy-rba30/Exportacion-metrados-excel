using System.Windows;
using ExportacionMetrados.Core.Metrado;

namespace ExportacionMetrados.UI
{
    /// <summary>Ventana del botón "Limpiar modelo": cuatro casillas con lo que se quita del proyecto.</summary>
    public partial class LimpiarModeloWindow : Window
    {
        public LimpiarModeloWindow()
        {
            InitializeComponent();
        }

        /// <summary>Opciones marcadas. Válido solo si ShowDialog devolvió true.</summary>
        public OpcionesLimpieza Opciones { get; } = new OpcionesLimpieza();

        private void BtnLimpiar_Click(object sender, RoutedEventArgs e)
        {
            Opciones.EliminarFiltros = ChkFiltros.IsChecked == true;
            Opciones.EliminarTablas = ChkTablas.IsChecked == true;
            Opciones.LimpiarValores = ChkValores.IsChecked == true;
            Opciones.BorrarParametros = ChkParametros.IsChecked == true;

            if (!Opciones.Alguna)
            {
                MessageBox.Show(this, "Marque al menos una opción.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string aviso = Opciones.BorrarParametros
                ? "Se quitarán del proyecto los parámetros del plugin y del contrato ARBA-comun y se perderán todos sus valores, " +
                  "incluidos los escritos por los add-ins ARBA.\n\n¿Continuar?"
                : "Se eliminará lo marcado. Se puede deshacer con Ctrl+Z.\n\n¿Continuar?";
            var r = MessageBox.Show(this, aviso, Title, MessageBoxButton.YesNo,
                Opciones.BorrarParametros ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            DialogResult = true;
        }
    }
}
