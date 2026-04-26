using Avalonia.Controls;
using DispatcherPanel.ViewModels;

namespace DispatcherPanel.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.ShutdownAsync();
        }
    }
}