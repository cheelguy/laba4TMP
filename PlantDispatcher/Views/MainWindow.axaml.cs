using Avalonia.Controls;
using PlantDispatcher.ViewModels;

namespace PlantDispatcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.ShutdownAsync();
        }
    }
}