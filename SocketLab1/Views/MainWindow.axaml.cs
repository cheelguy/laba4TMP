using Avalonia.Controls;
using SocketLab1.ViewModels;

namespace SocketLab1.Views;

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